using System.Text;
using System.Text.RegularExpressions;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Ingest;

public sealed record IngestResult(int SourceId, bool Ok, int Total, int Added, int Updated, string? Error);

/// <summary>
/// Pulls per-source channels (and optionally short EPG) from a provider credential and
/// upserts them. Keyed per source: a channel is identified by (SourceId, StreamId), so the
/// same logical channel exists as separate rows on each credential (the source-toggle model).
/// NOTE: this makes provider API calls and is therefore ONLY invoked on explicit request
/// (POST /api/sources/{id}/ingest), never automatically on startup.
/// </summary>
public sealed class IngestService
{
    private readonly DVarrDbContext _db;
    private readonly XtreamClient _xtream;
    private readonly DbWriteGate _gate;
    private readonly ILogger<IngestService> _log;

    public IngestService(DVarrDbContext db, XtreamClient xtream, DbWriteGate gate, ILogger<IngestService> log)
    {
        _db = db;
        _xtream = xtream;
        _gate = gate;
        _log = log;
    }

    public async Task<IngestResult> IngestSourceAsync(int sourceId, CancellationToken ct = default)
    {
        var s = await _db.Sources.FindAsync(new object?[] { sourceId }, ct);
        if (s is null) return new IngestResult(sourceId, false, 0, 0, 0, "source not found");
        if (!s.Enabled) return new IngestResult(sourceId, false, 0, 0, 0, "source is disabled — refusing to contact the provider");

        try
        {
            var auth = await _xtream.AuthAsync(s, ct);
            var streams = await _xtream.GetLiveStreamsAsync(s, ct);
            var cats = await _xtream.GetLiveCategoriesAsync(s, ct);
            var catMap = cats.Where(c => !string.IsNullOrWhiteSpace(c.CategoryId))
                .ToDictionary(c => c.CategoryId!, c => c.CategoryName ?? "", StringComparer.OrdinalIgnoreCase);
            string? Group(XtreamLiveStream st) =>
                st.CategoryId != null && catMap.TryGetValue(st.CategoryId, out var g) && !string.IsNullOrWhiteSpace(g) ? g : null;

            var existing = await _db.Channels
                .Where(c => c.SourceId == sourceId)
                .ToDictionaryAsync(c => c.StreamId, ct);

            int added = 0, updated = 0;
            var now = EpochTime.Now();

            await _gate.WriteAsync(async () =>
            {
                if (auth?.UserInfo is { } ui)
                {
                    s.ProviderActiveCons = ui.ActiveCons;
                    s.ProviderMaxConns = ui.MaxConnections;
                    s.Healthy = ui.Auth == 1;
                    s.Status = ui.Status;
                    s.AllowedOutputFormats = ui.AllowedOutputFormats is { Count: > 0 } ? string.Join(",", ui.AllowedOutputFormats) : null;
                    s.LastAuthAtUtc = now;
                    s.UpdatedUtc = now;
                }

                foreach (var st in streams)
                {
                    var norm = Norm(st.Name);
                    if (existing.TryGetValue(st.StreamId, out var ch))
                    {
                        ch.Name = st.Name ?? ch.Name;
                        ch.NameNorm = norm;
                        ch.LogicalKey = norm;
                        ch.EpgChannelId = st.EpgChannelId;
                        ch.GroupName = Group(st);
                        ch.ChannelNumber = st.Num;
                        ch.TvArchive = st.TvArchive != 0;
                        ch.TvArchiveDuration = st.TvArchiveDuration;
                        ch.DetectedQuality = DetectQuality(st.Name);
                        ch.UpdatedUtc = now;
                        updated++;
                    }
                    else
                    {
                        _db.Channels.Add(new Channel
                        {
                            SourceId = sourceId,
                            StreamId = st.StreamId,
                            Name = st.Name ?? $"#{st.StreamId}",
                            NameNorm = norm,
                            LogicalKey = norm,
                            EpgChannelId = st.EpgChannelId,
                            GroupName = Group(st),
                            ChannelNumber = st.Num,
                            TvArchive = st.TvArchive != 0,
                            TvArchiveDuration = st.TvArchiveDuration,
                            DetectedQuality = DetectQuality(st.Name),
                            Enabled = true,
                            CreatedUtc = now,
                            UpdatedUtc = now,
                        });
                        added++;
                    }
                }
                await _db.SaveChangesAsync(ct);
            }, ct);

            _log.LogInformation("[Ingest] Source {Id} ({Label}): {Total} streams ({Added} new, {Updated} updated)",
                sourceId, s.Label, streams.Count, added, updated);
            return new IngestResult(sourceId, true, streams.Count, added, updated, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Ingest] Source {Id} failed", sourceId);
            return new IngestResult(sourceId, false, 0, 0, 0, ex.Message);
        }
    }

    private static readonly Regex QualityToken = new(
        @"\b(uhd|fhd|hd|sd|4k|2160p?|1080p?|720p?|576p?|480p?|raw|h\.?265|hevc|h\.?264|avc|vip|backup)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^a-z0-9 ]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Normalised logical-channel key so per-source rows of the same channel group together (source toggle "all" view).</summary>
    internal static string Norm(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.ToLowerInvariant();
        // drop a leading country/provider prefix like "uk:", "au |", "us-"
        n = Regex.Replace(n, @"^\s*[a-z]{2,3}\s*[:|\-]\s*", "");
        n = QualityToken.Replace(n, " ");
        n = NonAlnum.Replace(n, " ");
        n = Spaces.Replace(n, " ").Trim();
        return n;
    }

    internal static string? DetectQuality(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var u = name.ToUpperInvariant();
        if (u.Contains("4K") || u.Contains("UHD") || u.Contains("2160")) return "2160p";
        if (u.Contains("FHD") || u.Contains("1080")) return "1080p";
        if (u.Contains(" HD") || u.Contains("-HD") || u.Contains("720")) return "720p";
        if (u.Contains(" SD") || u.Contains("480") || u.Contains("576")) return "SD";
        return null;
    }
}
