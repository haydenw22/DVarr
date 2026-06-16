using System.Text.Json;
using System.Text.Json.Serialization;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Ingest;

/// <summary>
/// One-time seed of ProviderSource rows from a local <c>sources.import.json</c> (the
/// gitignored export of Sportarr's IptvSources). Creates the credential rows ONLY — it
/// never contacts the provider. Runs on startup if no sources exist yet.
/// </summary>
public sealed class SourceSeeder
{
    private readonly DVarrDbContext _db;
    private readonly DbWriteGate _gate;
    private readonly ILogger<SourceSeeder> _log;

    public SourceSeeder(DVarrDbContext db, DbWriteGate gate, ILogger<SourceSeeder> log)
    {
        _db = db;
        _gate = gate;
        _log = log;
    }

    public async Task SeedFromFileAsync(string configDir, CancellationToken ct = default)
    {
        if (await _db.Sources.AnyAsync(ct)) return;

        var path = Path.Combine(configDir, "sources.import.json");
        if (!File.Exists(path))
        {
            _log.LogInformation("[Seed] No sources.import.json at {Path}; skipping source seed.", path);
            return;
        }

        List<SportarrSourceImport>? rows;
        try
        {
            await using var fs = File.OpenRead(path);
            rows = await JsonSerializer.DeserializeAsync<List<SportarrSourceImport>>(fs,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Seed] Failed to parse {Path}", path);
            return;
        }
        if (rows is null || rows.Count == 0) return;

        var now = EpochTime.Now();
        var created = 0;
        await _gate.WriteAsync(async () =>
        {
            foreach (var r in rows)
            {
                var (proto, host, port) = ParseEndpoint(r.Url);
                var label = string.IsNullOrWhiteSpace(r.Name) ? $"src{r.Id}" : r.Name!.Trim();
                _db.Sources.Add(new ProviderSource
                {
                    Label = label,
                    ServerProtocol = proto,
                    BaseUrl = host,
                    Port = port,
                    Username = r.Username ?? "",
                    Password = r.Password ?? "",
                    MaxStreams = 1,                       // provider-fixed (D6) regardless of source value
                    Enabled = r.IsActive != 0,
                    Healthy = false,                       // unknown until first auth (deferred until safe)
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });
                created++;
            }
            await _db.SaveChangesAsync(ct);
        }, ct);

        _log.LogInformation("[Seed] Seeded {Count} provider source(s) from {Path} (no provider calls made).", created, path);
    }

    /// <summary>Parse an Xtream/M3U URL into (protocol, host, port). Port 0 => provider default port.</summary>
    internal static (string proto, string host, int port) ParseEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ("http", "", 0);
        var u = url.Trim();
        if (!u.Contains("://", StringComparison.Ordinal)) u = "http://" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
        {
            var port = uri.IsDefaultPort ? 0 : uri.Port;
            return (uri.Scheme, uri.Host, port);
        }
        return ("http", url.Replace("http://", "").Replace("https://", "").Split('/')[0], 0);
    }
}

public sealed class SportarrSourceImport
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int Type { get; set; }
    public string? Url { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    [JsonPropertyName("MaxStreams")] public int MaxStreams { get; set; }
    public string? UserAgent { get; set; }
    public int IsActive { get; set; }
}
