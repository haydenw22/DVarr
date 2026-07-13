using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services;
using DVarr.Services.Events;
using DVarr.Services.Ingest;
using DVarr.Services.Media;
using DVarr.Services.Recording;
using DVarr.Services.Tuner;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

public static class ApiEndpoints
{
    public static void MapDVarrApi(this WebApplication app)
    {
        // ---- Sources (credentials are masked; never returned in full) ----
        app.MapGet("/api/sources", async (DVarrDbContext db, TunerLeaseManager tuner) =>
        {
            var sources = await db.Sources.OrderBy(s => s.Id).ToListAsync();
            // Pre-compute per-source counts with two awaited grouped queries, rather than 2 lazy synchronous Count()
            // calls per source evaluated on the JSON-serialization thread (blocking DB IO while writing the response).
            var chCounts = await db.Channels.GroupBy(c => c.SourceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.C);
            var pgCounts = await db.Programmes.GroupBy(p => p.SourceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.C);
            return Results.Json(sources.Select(s => new
            {
                s.Id, s.Label, s.Type, host = s.BaseUrl, s.Port, protocol = s.ServerProtocol,
                username = Mask(s.Username), hasPassword = !string.IsNullOrEmpty(s.Password),
                maxStreams = s.MaxStreams, s.Enabled, s.Healthy,
                expDateUtc = s.ExpDateUtc, isTrial = s.IsTrial, status = s.Status,
                epgUrl = s.EpgUrl, epgOverride = s.EpgOverride, userAgent = s.UserAgent,
                slotFree = tuner.IsFree(s.Id),
                channels = chCounts.GetValueOrDefault(s.Id),
                programmes = pgCounts.GetValueOrDefault(s.Id),
            }));
        });

        // Triggers a provider API call — invoked ONLY on explicit request, never on startup.
        app.MapPost("/api/sources/{id:int}/ingest", async (int id, IngestService ingest, CancellationToken ct) =>
        {
            var r = await ingest.IngestSourceAsync(id, ct);
            return r.Ok ? Results.Json(r) : Results.Json(r, statusCode: 502);
        });

        // Sync EPG (provider xmltv.php, or the source's external override URL) — contacts the provider.
        app.MapPost("/api/sources/{id:int}/epg", async (int id, EpgIngestService epg, CancellationToken ct) =>
        {
            var r = await epg.SyncSourceEpgAsync(id, ct);
            return r.Ok ? Results.Json(r) : Results.Json(r, statusCode: 502);
        });

        // Lightweight account refresh — auth ONLY (no channel/EPG pull), so it doesn't consume the credential's single
        // stream slot. Re-stamps the last-seen advisory fields (expiry, trial, status, connections, health) so the
        // Sources table's service-expiry can be updated cheaply without re-ingesting the full lineup.
        app.MapPost("/api/sources/{id:int}/refresh-account", async (int id, DVarrDbContext db, XtreamClient xtream, DbWriteGate gate, CancellationToken ct) =>
        {
            var s = await db.Sources.FindAsync(new object?[] { id }, ct);
            if (s is null) return Results.NotFound();
            if (!s.Enabled) return Results.Json(new { error = "source is disabled — refusing to contact the provider" }, statusCode: 409);
            try
            {
                var auth = await xtream.AuthAsync(s, ct);
                if (auth?.UserInfo is not { } ui)
                    return Results.Json(new { error = "provider did not return account info" }, statusCode: 502);
                var now = EpochTime.Now();
                await gate.WriteAsync(async () =>
                {
                    IngestService.ApplyUserInfo(s, ui, now);
                    await db.SaveChangesAsync(ct);
                }, ct);
                return Results.Json(new { ok = true, expDateUtc = s.ExpDateUtc, status = s.Status });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // ---- Source CRUD (set up / edit your own sources) ----
        app.MapPost("/api/sources", async (SourceUpsert req, DVarrDbContext db, DbWriteGate gate) =>
        {
            if (string.IsNullOrWhiteSpace(req.Host)) return Results.BadRequest(new { error = "host is required" });
            if (!ValidEpgUrl(req.EpgUrl)) return Results.BadRequest(new { error = "external EPG URL must be an absolute http(s) URL" });
            var label = string.IsNullOrWhiteSpace(req.Label) ? "source" : req.Label.Trim();
            // Label has a UNIQUE index — pre-check for a friendly 409 instead of a raw DB-constraint 500.
            if (await db.Sources.AnyAsync(x => x.Label == label)) return Results.Json(new { error = $"a source named '{label}' already exists" }, statusCode: 409);
            var now = EpochTime.Now();
            var s = new ProviderSource
            {
                Label = label,
                Type = string.IsNullOrWhiteSpace(req.Type) ? "xtream" : req.Type.Trim(),
                ServerProtocol = string.IsNullOrWhiteSpace(req.Protocol) ? "http" : req.Protocol.Trim(),
                BaseUrl = req.Host!.Trim(),
                Port = req.Port ?? 0,
                Username = req.Username ?? "",
                Password = req.Password ?? "",
                UserAgent = string.IsNullOrWhiteSpace(req.UserAgent) ? null : req.UserAgent!.Trim(),
                EpgUrl = string.IsNullOrWhiteSpace(req.EpgUrl) ? null : req.EpgUrl!.Trim(),
                EpgOverride = req.EpgOverride ?? false,
                MaxStreams = req.MaxStreams is > 0 ? req.MaxStreams!.Value : 1,
                Enabled = req.Enabled ?? true,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            await gate.WriteAsync(async () => { db.Sources.Add(s); await db.SaveChangesAsync(); });
            return Results.Json(new { s.Id });
        });

        app.MapPut("/api/sources/{id:int}", async (int id, SourceUpsert req, DVarrDbContext db, DbWriteGate gate) =>
        {
            var s = await db.Sources.FindAsync(id);
            if (s is null) return Results.NotFound();
            if (!ValidEpgUrl(req.EpgUrl)) return Results.BadRequest(new { error = "external EPG URL must be an absolute http(s) URL" });
            // Reject a rename onto another source's (unique) label with a friendly 409 rather than a DB 500.
            if (!string.IsNullOrWhiteSpace(req.Label) && req.Label.Trim() != s.Label
                && await db.Sources.AnyAsync(x => x.Id != id && x.Label == req.Label.Trim()))
                return Results.Json(new { error = $"a source named '{req.Label.Trim()}' already exists" }, statusCode: 409);
            await gate.WriteAsync(async () =>
            {
                if (!string.IsNullOrWhiteSpace(req.Label)) s.Label = req.Label.Trim();
                if (!string.IsNullOrWhiteSpace(req.Type)) s.Type = req.Type.Trim();
                if (!string.IsNullOrWhiteSpace(req.Protocol)) s.ServerProtocol = req.Protocol.Trim();
                if (req.Host != null) s.BaseUrl = req.Host.Trim();
                if (req.Port.HasValue) s.Port = req.Port.Value;             // partial-update safe
                if (!string.IsNullOrEmpty(req.Username)) s.Username = req.Username; // blank = keep existing
                if (!string.IsNullOrEmpty(req.Password)) s.Password = req.Password!; // blank = keep existing
                if (req.UserAgent != null) s.UserAgent = string.IsNullOrWhiteSpace(req.UserAgent) ? null : req.UserAgent.Trim();
                if (req.EpgUrl != null) s.EpgUrl = string.IsNullOrWhiteSpace(req.EpgUrl) ? null : req.EpgUrl.Trim(); // partial-safe (#17): only when sent
                if (req.EpgOverride.HasValue) s.EpgOverride = req.EpgOverride.Value;
                if (req.MaxStreams is > 0) s.MaxStreams = req.MaxStreams!.Value;
                if (req.Enabled.HasValue) s.Enabled = req.Enabled.Value;
                s.UpdatedUtc = EpochTime.Now();
                await db.SaveChangesAsync();
            });
            return Results.Json(new { s.Id, updated = true });
        });

        app.MapDelete("/api/sources/{id:int}", async (int id, DVarrDbContext db, DbWriteGate gate) =>
        {
            var s = await db.Sources.FindAsync(id);
            if (s is null) return Results.NotFound();
            var blocked = new[]
            {
                DVarr.Data.RecordingState.Pending, DVarr.Data.RecordingState.Starting, DVarr.Data.RecordingState.Recording,
                DVarr.Data.RecordingState.Recovering, DVarr.Data.RecordingState.FailingOver, DVarr.Data.RecordingState.Degraded,
                DVarr.Data.RecordingState.Stopping, DVarr.Data.RecordingState.Finalizing, DVarr.Data.RecordingState.Conflict,
            };
            if (await db.Recordings.AnyAsync(r => r.SourceId == id && blocked.Contains(r.State)))
                return Results.Json(new { error = "source has active or pending recordings" }, statusCode: 409);
            await gate.WriteAsync(async () =>
            {
                var chIds = await db.Channels.Where(c => c.SourceId == id).Select(c => c.Id).ToListAsync();
                await db.Programmes.Where(p => p.SourceId == id).ExecuteDeleteAsync(); // EPG is keyed per source
                // Drop the per-league mappings + health rows that point at these channels, or the resolver would
                // later try to score a channel that no longer exists (and the rows would be silent orphans).
                await db.LeagueChannelMaps.Where(m => chIds.Contains(m.ChannelId)).ExecuteDeleteAsync();
                await db.ChannelHealth.Where(h => chIds.Contains(h.ChannelId)).ExecuteDeleteAsync();
                await db.Channels.Where(c => c.SourceId == id).ExecuteDeleteAsync();
                db.Sources.Remove(s);
                await db.SaveChangesAsync();
            });
            return Results.Json(new { deleted = true });
        });

        // ---- Channels + source toggle (?source=all|<id>, ?q=) ----
        app.MapGet("/api/channels", async (DVarrDbContext db, string? source, string? q, string? group, int? take) =>
        {
            var query = from c in db.Channels
                        join s in db.Sources on c.SourceId equals s.Id
                        select new { c, sourceLabel = s.Label };
            if (!string.IsNullOrWhiteSpace(source) && source != "all" && int.TryParse(source, out var sid))
                query = query.Where(x => x.c.SourceId == sid);
            if (!string.IsNullOrWhiteSpace(group) && group != "all")
                query = query.Where(x => x.c.GroupName == group);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var ql = q.ToLower();
                query = query.Where(x => x.c.Name.ToLower().Contains(ql));
            }
            var rows = await query.OrderBy(x => x.c.Name).Take(take is > 0 and <= 2000 ? take.Value : 500).ToListAsync();
            return Results.Json(rows.Select(x => new
            {
                x.c.Id, x.c.Name, x.c.SourceId, x.sourceLabel, x.c.StreamId,
                quality = x.c.DetectedQuality, number = x.c.ChannelNumber, group = x.c.GroupName,
                logicalKey = x.c.LogicalKey, manual = !string.IsNullOrEmpty(x.c.DirectUrl),
            }));
        });

        // Distinct groups/categories, scoped to a source (drives the source-aware group filter).
        app.MapGet("/api/channels/groups", async (DVarrDbContext db, string? source) =>
        {
            var q = db.Channels.Where(c => c.GroupName != null && c.GroupName != "");
            if (!string.IsNullOrWhiteSpace(source) && source != "all" && int.TryParse(source, out var sid))
                q = q.Where(c => c.SourceId == sid);
            var groups = await q.Select(c => c.GroupName!).Distinct().OrderBy(g => g).ToListAsync();
            return Results.Json(groups);
        });

        // ---- Guide (timeline EPG): channels + their programmes in a window, joined by epg_channel_id, with
        //      a "now" anchor and overlapping recordings so the client can paint live=green / recording=red. ----
        app.MapGet("/api/guide", async (DVarrDbContext db, string? source, string? group, string? q, long? start, int? hours, int? take) =>
        {
            var now = EpochTime.Now();
            var winStart = start is > 0 ? start!.Value : now - 3600;        // default: 1h before now
            var span = Math.Clamp(hours ?? 6, 1, 24) * 3600L;
            var winEnd = winStart + span;

            // Channels to show (a single source makes the timeline coherent since EPG is per-source).
            var chq = db.Channels.AsQueryable();
            int? sid = (!string.IsNullOrWhiteSpace(source) && source != "all" && int.TryParse(source, out var s0)) ? s0 : null;
            if (sid is { } sv) chq = chq.Where(c => c.SourceId == sv);
            if (!string.IsNullOrWhiteSpace(group) && group != "all") chq = chq.Where(c => c.GroupName == group);
            if (!string.IsNullOrWhiteSpace(q)) { var ql = q.ToLower(); chq = chq.Where(c => c.Name.ToLower().Contains(ql)); }

            // Surface channels that CAN have guide data first (provider tvg-id OR a name-matched one), so the default
            // view isn't a wall of "no guide data".
            var chans = await chq
                .OrderByDescending(c => (c.EpgChannelId != null && c.EpgChannelId != "") || (c.MatchedEpgId != null && c.MatchedEpgId != ""))
                .ThenBy(c => c.Name)
                .Take(take is > 0 and <= 400 ? take.Value : 120)
                .Select(c => new { c.Id, c.Name, c.SourceId, c.EpgChannelId, c.MatchedEpgId, c.StreamId, group = c.GroupName, manual = c.DirectUrl != null && c.DirectUrl != "" })
                .ToListAsync();

            // Effective tvg-id = provider's epg_channel_id, else the name-matched one. Programme.EpgChannelId is
            // COLLATE NOCASE so the SQL IN is case-insensitive AND index-seekable; in-memory join key is lowercased.
            static string? Eff(string? provider, string? matched) => !string.IsNullOrEmpty(provider) ? provider : (string.IsNullOrEmpty(matched) ? null : matched);
            var epgIds = chans.Select(c => Eff(c.EpgChannelId, c.MatchedEpgId)).Where(e => e != null).Select(e => e!).Distinct().ToList();
            var srcIds = chans.Select(c => c.SourceId).Distinct().ToList();
            var progs = epgIds.Count == 0 ? new() : await db.Programmes
                .Where(p => srcIds.Contains(p.SourceId) && epgIds.Contains(p.EpgChannelId) && p.StopUtc > winStart && p.StartUtc < winEnd)
                .Select(p => new { p.Id, p.SourceId, p.EpgChannelId, p.StartUtc, p.StopUtc, p.Title })
                .ToListAsync();
            var progByKey = progs.GroupBy(p => (p.SourceId, key: p.EpgChannelId.ToLowerInvariant()))
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.StartUtc).ToList());

            // Recordings (any non-terminal/active or done) overlapping the window on the shown channels → red.
            var chIds = chans.Select(c => c.Id).ToList();
            var recs = await db.Recordings
                .Where(r => chIds.Contains(r.ChannelId) && r.EndUtc + r.PostPadS > winStart && r.StartUtc - r.PrePadS < winEnd)
                // start/end = padded window (pre-roll … post-roll); coreStart/coreEnd = the actual event window.
                .Select(r => new { r.ChannelId, start = r.StartUtc - r.PrePadS, end = r.EndUtc + r.PostPadS, coreStart = r.StartUtc, coreEnd = r.EndUtc, state = r.State.ToString() })
                .ToListAsync();

            return Results.Json(new
            {
                now,
                windowStart = winStart,
                windowEnd = winEnd,
                source = sid,
                channels = chans.Select(c => new
                {
                    channelId = c.Id, c.Name, c.SourceId, c.StreamId, c.group, c.manual, epgChannelId = Eff(c.EpgChannelId, c.MatchedEpgId),
                    programmes = (Eff(c.EpgChannelId, c.MatchedEpgId) is { } eff && progByKey.TryGetValue((c.SourceId, eff.ToLowerInvariant()), out var ps))
                        ? ps.Select(p => new { p.Id, start = p.StartUtc, stop = p.StopUtc, p.Title })
                        : Enumerable.Empty<object>().Select(_ => new { Id = 0, start = 0L, stop = 0L, Title = "" }),
                    recordings = recs.Where(r => r.ChannelId == c.Id).Select(r => new { r.start, r.end, r.coreStart, r.coreEnd, r.state }),
                }),
            });
        });

        // ---- Recordings ----
        app.MapGet("/api/recordings", async (DVarrDbContext db) =>
        {
            // LIVE/UPCOMING rows are returned uncapped (they're naturally few and are exactly what must never fall
            // off the list); the 200-row window applies only to TERMINAL history. Previously one descending
            // Take(200) meant >200 far-future schedules pushed imminent/active rows out entirely (audit API-REC-01).
            // DONE rows are not returned at all: a finished recording graduates to the Library (/api/library),
            // which tracks the actual files — the Recordings page is the request/capture pipeline only. The Done
            // rows themselves stay in the DB as the scheduler's dedupe history.
            var terminal = new[] { RecordingState.NeedsAttention, RecordingState.Missed, RecordingState.Cancelled };
            var q0 = db.Recordings.Where(r => r.State != RecordingState.Done);
            var q = from r in q0
                    join ch in db.Channels on r.ChannelId equals ch.Id into chj
                    from ch in chj.DefaultIfEmpty()
                    join s in db.Sources on r.SourceId equals s.Id into sj
                    from s in sj.DefaultIfEmpty()
                    select new
                    {
                        r.Id, r.Title, StateEnum = r.State,
                        channel = ch != null ? ch.Name : null, source = s != null ? s.Label : null,
                        r.StartUtc, r.EndUtc, r.PrePadS, r.PostPadS,
                        r.BytesWritten, r.AttemptCount, r.OutputPath, r.FailureReason,
                        r.EventId,
                    };
            var live = await q.Where(x => !terminal.Contains(x.StateEnum)).OrderByDescending(x => x.StartUtc).ToListAsync();
            var history = await q.Where(x => terminal.Contains(x.StateEnum)).OrderByDescending(x => x.StartUtc).Take(200).ToListAsync();
            var rows = live.Concat(history).OrderByDescending(x => x.StartUtc).ToList();

            // League per row (EventId → Event.LeagueId → League.Name), batched as two small IN lookups over the ≤200
            // returned rows and stitched in memory — no per-row query. Manual recordings (no EventId) get nulls.
            var evIds = rows.Where(r => r.EventId != null).Select(r => r.EventId!.Value).Distinct().ToList();
            var evLeague = evIds.Count == 0
                ? new Dictionary<int, int>()
                : await db.Events.Where(e => evIds.Contains(e.Id)).Select(e => new { e.Id, e.LeagueId }).ToDictionaryAsync(x => x.Id, x => x.LeagueId);
            var lgIds = evLeague.Values.Distinct().ToList();
            var lgName = lgIds.Count == 0
                ? new Dictionary<int, string>()
                : await db.Leagues.Where(l => lgIds.Contains(l.Id)).Select(l => new { l.Id, l.Name }).ToDictionaryAsync(x => x.Id, x => x.Name);

            return Results.Json(rows.Select(r =>
            {
                int? leagueId = r.EventId is { } eid && evLeague.TryGetValue(eid, out var lid) ? lid : null;
                return new
                {
                    r.Id, r.Title, state = r.StateEnum.ToString(), r.channel, r.source,
                    r.StartUtc, r.EndUtc, r.PrePadS, r.PostPadS,
                    r.BytesWritten, r.AttemptCount, r.OutputPath, r.FailureReason,
                    leagueId,
                    league = leagueId is { } l && lgName.TryGetValue(l, out var ln) ? ln : null,
                };
            }));
        });

        app.MapGet("/api/recordings/{id:int}", async (int id, DVarrDbContext db, RecorderService rec) =>
        {
            var r = await db.Recordings.FindAsync(id);
            if (r is null) return Results.NotFound();
            var segs = await db.RecordingSegments.CountAsync(s => s.RecordingId == id);
            var notes = await db.Notifications.Where(n => n.RecordingId == id)
                .OrderByDescending(n => n.TsUtc).Take(20)
                .Select(n => new { n.TsUtc, kind = n.Kind.ToString(), severity = n.Severity.ToString(), n.Message })
                .ToListAsync();
            return Results.Json(new
            {
                r.Id, r.Title, state = r.State.ToString(), live = rec.IsActive(id),
                r.SourceId, r.ChannelId, r.StreamId, r.StartUtc, r.EndUtc, r.PrePadS, r.PostPadS,
                r.BytesWritten, r.AttemptCount, r.OutputPath, r.FailureReason, segments = segs, notifications = notes,
            });
        });

        app.MapPost("/api/recordings", async (CreateRecordingRequest req, DVarrDbContext db, DbWriteGate gate, SettingsService settings) =>
        {
            var ch = await db.Channels.FindAsync(req.ChannelId);
            if (ch is null) return Results.BadRequest(new { error = "channel not found" });
            // A recording on a disabled channel/source can never lease a tuner — reject at schedule time rather than
            // letting it sit Pending and silently miss. (The test-recording flow uses its own always-enabled source.)
            var recSrc = await db.Sources.FindAsync(ch.SourceId);
            if (!ch.Enabled || recSrc is null || !recSrc.Enabled) return Results.BadRequest(new { error = "channel's source is disabled" });
            if (req.EndUtc <= req.StartUtc) return Results.BadRequest(new { error = "end time must be after start time" });
            var pre = req.PrePadS ?? await settings.GetIntAsync("default_pre_pad_s");
            var post = req.PostPadS ?? await settings.GetIntAsync("default_post_pad_s");
            var now = EpochTime.Now();
            var rec = new Recording
            {
                ChannelId = ch.Id, SourceId = ch.SourceId, StreamId = ch.StreamId,
                StartUtc = req.StartUtc, EndUtc = req.EndUtc, PrePadS = pre, PostPadS = post,
                Title = string.IsNullOrWhiteSpace(req.Title) ? ch.Name : req.Title,
                // Optional TheSportsDB match for a Plex-clean rename at finalize (manual recordings have no Event).
                MatchQuery = string.IsNullOrWhiteSpace(req.MatchQuery) ? null : req.MatchQuery!.Trim(),
                Priority = ParsePriority(req.Priority), State = RecordingState.Pending,
                CreatedUtc = now, UpdatedUtc = now,
            };
            await gate.WriteAsync(async () => { db.Recordings.Add(rec); await db.SaveChangesAsync(); });
            return Results.Json(new { rec.Id, state = rec.State.ToString() });
        });

        // One-click test against a public stream — creates a "Manual / Test" source + channel,
        // schedules a recording now for N minutes. Does NOT touch the provider.
        app.MapPost("/api/test/recording", async (TestRecordingRequest req, DVarrDbContext db, DbWriteGate gate) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest(new { error = "url required" });
            var now = EpochTime.Now();
            var minutes = Math.Clamp(req.Minutes ?? 2, 1, 180);

            Recording rec = null!;
            await gate.WriteAsync(async () =>
            {
                var src = await db.Sources.FirstOrDefaultAsync(s => s.Label == "Manual / Test");
                if (src is null)
                {
                    src = new ProviderSource { Label = "Manual / Test", ServerProtocol = "http", BaseUrl = "manual", Port = 0, MaxStreams = 1, Enabled = true, Healthy = true, CreatedUtc = now, UpdatedUtc = now };
                    db.Sources.Add(src);
                    await db.SaveChangesAsync();
                }
                var name = string.IsNullOrWhiteSpace(req.Name) ? "Test capture" : req.Name!;
                // Reuse the single (Manual/Test, StreamId 0) channel across tests (refresh its URL/name) instead of
                // adding a new row every time — avoids accumulating throwaway test channels.
                var ch = await db.Channels.FirstOrDefaultAsync(c => c.SourceId == src.Id && c.StreamId == 0);
                if (ch is null) { ch = new Channel { SourceId = src.Id, StreamId = 0, CreatedUtc = now }; db.Channels.Add(ch); }
                ch.Name = name; ch.NameNorm = name.ToLower(); ch.LogicalKey = name.ToLower(); ch.DirectUrl = req.Url; ch.Enabled = true; ch.UpdatedUtc = now;
                await db.SaveChangesAsync();
                rec = new Recording
                {
                    ChannelId = ch.Id, SourceId = src.Id, StreamId = 0,
                    StartUtc = now, EndUtc = now + minutes * 60, PrePadS = 0, PostPadS = 0,
                    Title = name, Priority = RecordingPriority.Normal, State = RecordingState.Pending,
                    CreatedUtc = now, UpdatedUtc = now,
                };
                db.Recordings.Add(rec);
                await db.SaveChangesAsync();
            });
            return Results.Json(new { rec.Id, minutes, message = "scheduled now; the scheduler will start it within a few seconds" });
        });

        app.MapPost("/api/recordings/{id:int}/stop", async (int id, RecorderService rec, DVarrDbContext db, DbWriteGate gate, ILoggerFactory lf) =>
        {
            var r = await db.Recordings.FindAsync(id);
            if (r is null) return Results.NotFound();
            var active = rec.IsActive(id);
            lf.CreateLogger("DVarr.Api").LogInformation("[Api] Stop requested for recording {Id} (state {State}, active {Active})", id, r.State, active);

            // Live capture → cancel; the supervisor stops ffmpeg and finalizes the partial recording to Done.
            if (active) { await rec.StopAsync(id); return Results.Json(new { stopping = true }); }

            // Not actively captured. If it's still non-terminal (Pending, Conflict, or an orphaned active state left by a
            // crash/restart where the supervisor isn't running it), cancel it cleanly so the UI always clears it and the
            // scheduler won't keep trying to arm it. (Previously only Pending was handled — Conflict/orphans hit noop.)
            var terminal = new[] { RecordingState.Done, RecordingState.Missed, RecordingState.Cancelled, RecordingState.NeedsAttention };
            if (!terminal.Contains(r.State))
            {
                await gate.WriteAsync(async () =>
                {
                    var rr = await db.Recordings.FindAsync(id);
                    if (rr is null) return;
                    rr.State = RecordingState.Cancelled; rr.UpdatedUtc = EpochTime.Now(); rr.FailureReason = "cancelled by user";
                    db.Notifications.Add(new Notification { RecordingId = id, TsUtc = EpochTime.Now(), Kind = NotificationKind.Cancelled, Severity = Severity.Info, ToState = "Cancelled", Message = "cancelled by user" });
                    await db.SaveChangesAsync();
                });
                return Results.Json(new { cancelled = true });
            }
            return Results.Json(new { noop = true, state = r.State.ToString() });
        });

        // Start a Pending/Conflict recording NOW (early / manual) — force-arm before its pre-roll. Uses the recording's
        // own window end, and benefits from the same cross-login spreading as the scheduler if its credential is busy.
        app.MapPost("/api/recordings/{id:int}/start", async (int id, RecorderService rec, DVarrDbContext db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var r = await db.Recordings.FindAsync(id);
            if (r is null) return Results.NotFound();
            if (rec.IsActive(id)) return Results.Json(new { already = true, state = r.State.ToString() });
            if (r.State is not (RecordingState.Pending or RecordingState.Conflict))
                return Results.Json(new { error = $"can't start a recording in state {r.State}" }, statusCode: 409);
            if (r.EndUtc + r.PostPadS <= EpochTime.Now())
                return Results.Json(new { error = "this recording's window has already passed" }, statusCode: 409);
            lf.CreateLogger("DVarr.Api").LogInformation("[Api] Manual start requested for recording {Id} (state {State})", id, r.State);
            var err = await rec.TryStartAsync(id, ct);
            return err is null ? Results.Json(new { started = true }) : Results.Json(new { error = err }, statusCode: 409);
        });

        // ---- Manual import / assignment (sort a staged recording onto a TheSportsDB game) ----
        // Candidate games for a league. Prefer the LOCAL events of an added league: with the premium full-season sync
        // the local set is complete (fixes the old "dropdown missing games" caused by the dropped-game day-sweep), it's
        // instant, and its ids are exactly what AssignAsync numbers. Fall back to a live full-season fetch for a league
        // that isn't added locally. The `date` hint is no longer needed — the whole season is returned, start-ordered.
        app.MapGet("/api/import/events", async (string leagueId, string? date, DVarrDbContext db, TheSportsDbClient tsdb, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(leagueId)) return Results.BadRequest(new { error = "leagueId required" });
            var local = await db.Leagues.FirstOrDefaultAsync(l => l.ExternalLeagueId == leagueId, ct);
            if (local is not null)
            {
                var evs = await db.Events.Where(e => e.LeagueId == local.Id && e.TsdbEventId != null)
                    .OrderBy(e => e.StartUtc)
                    .Select(e => new { id = e.TsdbEventId!, title = e.Title, date = e.StartUtc, round = e.Round })
                    .ToListAsync(ct);
                if (evs.Count > 0) return Results.Json(evs);
            }
            var year = (DateTime.TryParse(date, out var dy) ? dy.Year : EpochTime.ToDisplay(EpochTime.Now()).Year).ToString();
            var live = (await tsdb.GetSeasonEventsAsync(leagueId, year, ct)).OrderBy(e => e.StartUtc ?? 0L)
                .Select(e => new { id = e.Id, title = e.Title, date = e.StartUtc, round = e.Round }).ToList();
            return Results.Json(live);
        });

        // Re-file a staged (.unsorted) recording onto the chosen game → moves it into the Plex League/Season/Game layout.
        app.MapPost("/api/recordings/{id:int}/import", async (int id, ImportAssignRequest req, MediaImportService media, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LeagueId) || string.IsNullOrWhiteSpace(req.EventId))
                return Results.BadRequest(new { error = "leagueId and eventId are required" });
            var (ok, path, error) = await media.AssignAsync(id, req.LeagueId!.Trim(), req.EventId!.Trim(), ct);
            return ok ? Results.Json(new { ok = true, path }) : Results.Json(new { error }, statusCode: 400);
        });

        app.MapDelete("/api/recordings/{id:int}", async (int id, bool? keepFile, RecorderService rec, DVarrDbContext db, DbWriteGate gate, RuntimePaths paths, ILoggerFactory lf) =>
        {
            var r = await db.Recordings.FindAsync(id);
            if (r is null) return Results.NotFound();
            // If it's live, stop it and only remove the row once the supervisor has actually settled — otherwise a
            // long finalize would keep running against a deleted row (orphaning the output file + no-op state writes).
            if (rec.IsActive(id))
            {
                var settled = await rec.StopAsync(id);
                if (!settled)
                    return Results.Json(new { deleted = false, finalizing = true, error = "recording is still finalizing — try delete again in a moment" }, statusCode: 409);
            }
            // Snapshot the disk artifacts from a FRESH read, not the entity tracked above (audit REC-01): StopAsync
            // settles only after finalize + media import, and the import MOVES the file and rewrites OutputPath in a
            // different scope — the tracked row still holds the pre-import flat path, so cleaning up from it would
            // silently orphan the real imported MKV. (Defaults to deleting the files — that's what "delete a
            // recording" means to users; ?keepFile=true removes just the DVarr entry.)
            var fresh = await db.Recordings.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.OutputPath, x.SegmentDir }).FirstOrDefaultAsync();
            var outputPath = fresh?.OutputPath ?? r.OutputPath;
            var segDir = fresh?.SegmentDir ?? r.SegmentDir;
            await gate.WriteAsync(async () => { db.Recordings.Remove(r); await db.SaveChangesAsync(); });
            // File deletion happens AFTER the row commit so a disk error can never strand a half-deleted entry;
            // cleanup problems are reported in the response (audit REC-04) instead of silently logged.
            string? cleanupError = null;
            if (keepFile != true)
            {
                cleanupError = DeleteRecordingArtifacts(outputPath, segDir, paths, lf.CreateLogger("Recordings"));
                // Keep the library truthful: the file is gone, so its library row goes too. (keepFile=true keeps
                // the row — the FK just severed to null and the file lives on as a library item.)
                if (!string.IsNullOrWhiteSpace(outputPath))
                    await gate.WriteAsync(async () =>
                    {
                        await db.LibraryItems.Where(i => i.FilePath == outputPath).ExecuteDeleteAsync();
                    });
            }
            return Results.Json(new { deleted = true, fileCleanupError = cleanupError });
        });

        // ---- Conflict planning ----
        // Per-credential timeline (each login = one stream slot) + the list of conflicted recordings with reasons.
        app.MapGet("/api/conflicts", async (DVarrDbContext db) =>
        {
            var now = EpochTime.Now();
            var horizonEnd = now + 14L * 86400;
            var slotStates = new[] { RecordingState.Pending, RecordingState.Starting, RecordingState.Recording,
                RecordingState.Recovering, RecordingState.FailingOver, RecordingState.Degraded, RecordingState.Stopping, RecordingState.Finalizing };
            var sources = await db.Sources.Where(s => s.Enabled).OrderBy(s => s.Id).ToListAsync();
            var recs = await db.Recordings
                .Where(r => slotStates.Contains(r.State) && r.EndUtc + r.PostPadS > now && r.StartUtc - r.PrePadS < horizonEnd)
                .ToListAsync();
            var chNames = await db.Channels.Where(c => recs.Select(r => r.ChannelId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
            var credentials = sources.Select(s => new
            {
                s.Id, label = s.Label,
                load = recs.Where(r => r.SourceId == s.Id).OrderBy(r => r.StartUtc).Select(r => new
                {
                    r.Id, r.Title, state = r.State.ToString(),
                    channel = chNames.TryGetValue(r.ChannelId, out var n) ? n : null,
                    winStart = r.StartUtc - r.PrePadS, winEnd = r.EndUtc + r.PostPadS, r.StartUtc, r.EndUtc,
                }).ToList(),
            }).ToList();
            var conflicts = await db.Recordings.Where(r => r.State == RecordingState.Conflict)
                .OrderBy(r => r.StartUtc)
                .Select(r => new { r.Id, r.Title, r.StartUtc, r.EndUtc, reason = r.FailureReason })
                .ToListAsync();
            return Results.Json(new { now, credentials, conflicts });
        });

        // Read-only "where would this land?" for the Schedule modal: free credential, spread to the other login, or conflict.
        app.MapGet("/api/recordings/plan-preview", async (int channelId, long startUtc, long endUtc, DVarrDbContext db, ResolverService resolver, SettingsService settings) =>
        {
            var ch = await db.Channels.FindAsync(channelId);
            if (ch is null) return Results.Json(new { ok = false, badge = "channel not found" });
            var pre = await settings.GetIntAsync("default_pre_pad_s");
            var post = await settings.GetIntAsync("default_post_pad_s");
            var winStart = startUtc - pre; var winEnd = endUtc + post;
            var slotStates = new[] { RecordingState.Pending, RecordingState.Starting, RecordingState.Recording,
                RecordingState.Recovering, RecordingState.FailingOver, RecordingState.Degraded, RecordingState.Stopping, RecordingState.Finalizing };
            var committed = await db.Recordings.Where(r => slotStates.Contains(r.State))
                .Select(r => new { r.SourceId, S = r.StartUtc - r.PrePadS, E = r.EndUtc + r.PostPadS }).ToListAsync();
            bool Busy(int sid) => committed.Any(c => c.SourceId == sid && c.S < winEnd && winStart < c.E);

            var labels = await db.Sources.ToDictionaryAsync(s => s.Id, s => s.Label);
            string Lbl(int sid) => labels.TryGetValue(sid, out var l) ? l : $"#{sid}";

            if (!Busy(ch.SourceId)) return Results.Json(new { ok = true, badge = $"will record on {Lbl(ch.SourceId)}" });
            foreach (var e in await resolver.EquivalentChannelsAsync(ch.Id))
                if (!Busy(e.SourceId))
                    return Results.Json(new { ok = true, spread = true, badge = $"{Lbl(ch.SourceId)} busy → will record on {Lbl(e.SourceId)}" });
            return Results.Json(new { ok = false, conflict = true, badge = "both logins busy → CONFLICT" });
        });

        // Manual override from the Conflicts view: move a pending/conflicted recording to another login and/or bump priority.
        app.MapPost("/api/recordings/{id:int}/reassign", async (int id, ReassignRequest req, DVarrDbContext db, DbWriteGate gate, ResolverService resolver) =>
        {
            var r = await db.Recordings.FindAsync(id);
            if (r is null) return Results.NotFound();
            if (r.State is not (RecordingState.Pending or RecordingState.Conflict))
                return Results.Json(new { error = "only pending or conflicted recordings can be reassigned" }, statusCode: 409);
            var now = EpochTime.Now();
            // If a specific login was requested it MUST have an equivalent channel for this recording — otherwise the
            // reassign is impossible and we must say so. (Previously this returned ok=true and a "Reassigned" toast
            // while the recording silently stayed in Conflict.) Resolve before the gate so we can 409 without mutating.
            ResolvedChannel? equiv = null;
            if (req.SourceId is { } sid && sid != r.SourceId)
            {
                equiv = (await resolver.EquivalentChannelsAsync(r.ChannelId)).FirstOrDefault(x => x.SourceId == sid);
                if (equiv is null)
                    return Results.Json(new { error = "that login has no equivalent channel for this recording" }, statusCode: 409);
            }
            var aborted = false;
            var placed = false;
            await gate.WriteAsync(async () =>
            {
                // Re-validate inside the gate: the chosen login could have been disabled between EquivalentChannelsAsync
                // (un-gated) and here — don't re-point onto a now-disabled credential.
                if (equiv is not null)
                {
                    var es = await db.Sources.FindAsync(equiv.SourceId);
                    if (es is null || !es.Enabled) { aborted = true; return; }
                }
                if (req.Priority is not null) r.Priority = ParsePriority(req.Priority);
                if (equiv is not null)
                {
                    // SourceId is part of the (Id, SourceId) alternate key — re-point via RecordingRepoint (it can't be
                    // changed on the tracked entity). Drops the auto-fallbacks too (a credential change invalidates them).
                    await RecordingRepoint.ApplyAsync(db, id, equiv.SourceId, equiv.ChannelId, equiv.StreamId, now);
                    r.ChannelLocked = true; // a manual placement is durable — the arm-window EPG re-pick must not move it
                    placed = true;
                }
                // Only unpark a Conflict to Pending on an EXPLICIT placement (the user picked a specific login). A
                // bare priority bump keeps it in Conflict so the auto-scheduler's conflict re-evaluation re-plans it
                // with the new priority next tick — and can preempt a lower-priority slot holder — instead of dropping
                // it into Pending where nothing re-plans it and it would just sit until Missed.
                if (r.State == RecordingState.Conflict && placed) { r.State = RecordingState.Pending; r.FailureReason = null; }
                r.UpdatedUtc = now;
                await db.SaveChangesAsync();
            });
            if (aborted) return Results.Json(new { error = "that login became disabled — retry" }, statusCode: 409);
            // `r` is the tracked entity; RecordingRepoint changed SourceId/ChannelId via a tracker-bypassing UPDATE,
            // so report the new values explicitly (the tracked copy is stale for those fields).
            return Results.Json(new { ok = true, state = r.State.ToString(), SourceId = placed ? equiv!.SourceId : r.SourceId, ChannelId = placed ? equiv!.ChannelId : r.ChannelId });
        });

        // Re-resolve a scheduled recording's channel against the league's CURRENT mapping (e.g. after you re-pin the
        // channel) — updates channel/source/stream + the same-credential fallback ladder IN PLACE, no delete/recreate.
        // Pending/Conflict only (never an active capture); event-linked only (a manual recording has no league mapping).
        app.MapPost("/api/recordings/{id:int}/resolve", async (int id, DVarrDbContext db, DbWriteGate gate, ResolverService resolver) =>
        {
            var r = await db.Recordings.FindAsync(id);
            if (r is null) return Results.NotFound();
            if (r.State is not (RecordingState.Pending or RecordingState.Conflict))
                return Results.Json(new { error = "only pending or conflicted recordings can be re-resolved" }, statusCode: 409);
            if (r.EventId is not { } eid)
                return Results.Json(new { error = "manual recordings have no league mapping to re-resolve" }, statusCode: 400);
            var res = await resolver.ResolveAsync(eid);
            if (!res.Ok || res.Primary is null)
                return Results.Json(new { error = res.Reason ?? "could not resolve a channel" }, statusCode: 409);
            var p = res.Primary;
            var changed = p.ChannelId != r.ChannelId;
            var now = EpochTime.Now();
            var aborted = false;
            await gate.WriteAsync(async () =>
            {
                // Re-validate inside the gate: the resolved source/channel could have been disabled between the
                // (un-gated) resolver read and here — don't re-point a recording onto a now-disabled credential.
                var src = await db.Sources.FindAsync(p.SourceId);
                var rch = await db.Channels.FindAsync(p.ChannelId);
                if (src is null || !src.Enabled || rch is null || !rch.Enabled) { aborted = true; return; }
                // SourceId is part of the (Id, SourceId) alternate key — re-point via RecordingRepoint (deletes the old
                // fallbacks + applies a tracker-bypassing UPDATE), then rewrite the failover ladder so a failover can't
                // fall back to the OLD channel (resolver fallbacks are already restricted to the winner's credential).
                await RecordingRepoint.ApplyAsync(db, id, p.SourceId, p.ChannelId, p.StreamId, now);
                var rank = 2; // rank 1 is the primary (carried on Recording.ChannelId); RecorderService loads fallbacks at Rank >= 2
                foreach (var fb in res.Fallbacks.Where(f => f.ChannelId != p.ChannelId))
                    db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = id, Rank = rank++, ChannelId = fb.ChannelId, SourceId = fb.SourceId });
                r.ResolutionSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    resolved_channel_id = p.ChannelId, channel = p.ChannelName, resolver_version = 2, resolved_at = now, reresolved = true,
                });
                await db.SaveChangesAsync();
            });
            if (aborted) return Results.Json(new { error = "resolved source/channel became disabled — retry" }, statusCode: 409);
            // r.SourceId on the tracked entity is stale (RecordingRepoint updated it via a tracker-bypassing UPDATE).
            return Results.Json(new { ok = true, changed, channelId = p.ChannelId, channel = p.ChannelName, SourceId = p.SourceId });
        });

        // ---- Settings ----
        app.MapGet("/api/settings", async (SettingsService settings) => Results.Json(await settings.GetAllAsync()));
        app.MapPut("/api/settings", async (Dictionary<string, string> values, SettingsService settings) =>
        {
            // Allowlist keys against the known defaults (no unbounded Settings-table growth / namespace pollution) and
            // require an int value for int-typed keys (GetIntAsync silently returns 0 on a non-numeric value, which
            // would disable padding/intervals).
            foreach (var kv in values)
            {
                if (!SettingsService.Defaults.TryGetValue(kv.Key, out var def))
                    return Results.Json(new { error = $"unknown setting key: {kv.Key}" }, statusCode: 400);
                if (int.TryParse(def, out _))
                {
                    // Int-typed keys are all counts/seconds/intervals/caps/thresholds — a negative would be stored and
                    // used raw (e.g. pre<0 makes winStart = start - pre run FORWARD), so require >= 0. The ONE signed key
                    // is epg_auto_sync_offset_minutes (a UTC offset; the Americas need it negative, range-checked at use).
                    var signed = kv.Key == "epg_auto_sync_offset_minutes";
                    if (!int.TryParse(kv.Value, out var iv) || (!signed && iv < 0))
                        return Results.Json(new { error = signed ? $"setting '{kv.Key}' must be an integer" : $"setting '{kv.Key}' must be a non-negative integer" }, statusCode: 400);
                }
                // Time-of-day settings must be HH:MM (a junk value would otherwise be accepted, shown in the UI, and
                // silently fall back to the default at runtime).
                if (kv.Key == "epg_auto_sync_time" && !System.Text.RegularExpressions.Regex.IsMatch(kv.Value.Trim(), "^([01]?[0-9]|2[0-3]):[0-5][0-9]$"))
                    return Results.Json(new { error = $"setting '{kv.Key}' must be a time in HH:MM (24-hour) format" }, statusCode: 400);
                // The display timezone must be a real IANA zone id — a typo would silently fall back to fixed +10
                // everywhere times are shown or files are dated, which is exactly the bug this setting exists to fix.
                if (kv.Key == "timezone_display" && EpochTime.ResolveZone(kv.Value) is null)
                    return Results.Json(new { error = $"'{kv.Value}' is not a recognised timezone — use an IANA name like Australia/Brisbane or America/New_York" }, statusCode: 400);
            }
            await settings.SetManyAsync(values); // one transaction — all keys commit or none do (audit SET-03)
            // Apply a timezone change immediately (UI clock, filenames, Plex air dates) — no restart needed.
            if (values.TryGetValue("timezone_display", out var tzId)) EpochTime.SetDisplayZone(tzId);
            return Results.Json(await settings.GetAllAsync());
        });

        // ---- Activity feed ----
        app.MapGet("/api/notifications", async (DVarrDbContext db, int? take) =>
        {
            var n = await db.Notifications
                .OrderByDescending(x => x.TsUtc)
                .Take(take is > 0 and <= 200 ? take.Value : 60)
                .Select(x => new { x.Id, x.TsUtc, x.RecordingId, kind = x.Kind.ToString(), severity = x.Severity.ToString(), x.FromState, x.ToState, x.Message })
                .ToListAsync();
            return Results.Json(n);
        });

        app.MapGet("/api/ticks", async (DVarrDbContext db, int? take) =>
        {
            var t = await db.ScheduleTicks
                .OrderByDescending(x => x.TickUtc)
                .Take(take is > 0 and <= 100 ? take.Value : 20)
                .Select(x => new { x.Id, x.TickUtc, x.RecordingsExamined, x.Started, x.Resumed, x.Finalized, x.Missed, x.Conflicts, x.DurationMs })
                .ToListAsync();
            return Results.Json(t);
        });

        // ---- SSE live recording status ----
        app.MapGet("/api/stream/recordings", async (HttpContext ctx, RecordingEventBus bus, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");
            var (id, reader) = bus.Subscribe();
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                await foreach (var msg in reader.ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync($"data: {msg}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { bus.Unsubscribe(id); }
        });
    }

    /// <summary>
    /// Remove a deleted recording's disk artifacts: the output file plus its same-basename sidecars (the .nfo and
    /// thumbnail the media import wrote next to it), the per-game folder IF the delete emptied it (show-level
    /// artwork lives a level up and is shared, so it's never touched — and the media/segment ROOTS themselves are
    /// never deleted, however empty), and the per-recording segment scratch. Every step is best-effort — a
    /// locked/missing file never fails the API delete — but failures are RETURNED so the UI can say the entry was
    /// removed while the file wasn't (audit REC-04). Returns null when everything cleaned up.
    /// </summary>
    private static string? DeleteRecordingArtifacts(string? outputPath, string? segDir, RuntimePaths paths, ILogger log)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var errors = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath))
            {
                var dir = Path.GetDirectoryName(outputPath);
                var baseName = Path.GetFileNameWithoutExtension(outputPath);
                File.Delete(outputPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    // String-prefix match rather than a glob: the basename contains bracketed stamps ("[2026-07-12_1400]")
                    // that are glob-hostile, and only metadata extensions are eligible so a neighbouring video is safe.
                    foreach (var side in Directory.EnumerateFiles(dir)
                                 .Where(f => Path.GetFileName(f).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
                                             && Path.GetExtension(f).ToLowerInvariant() is ".nfo" or ".jpg" or ".jpeg" or ".png"))
                        File.Delete(side);
                    // Prune the emptied per-game folder — but NEVER the media root itself (a flat layout would
                    // otherwise let an emptied library delete the mount point's directory).
                    if (!string.Equals(Norm(dir), Norm(paths.MediaDir), StringComparison.OrdinalIgnoreCase)
                        && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                log.LogInformation("[Recordings] deleted file + sidecars for {Path}", outputPath);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[Recordings] file cleanup failed for {Path} (entry already deleted)", outputPath);
            errors.Add($"the recorded file couldn't be removed ({ex.Message})");
        }
        try
        {
            // segDir = /segments/{id}/A — remove the whole per-recording scratch dir, mirroring finalize's cleanup.
            // Same root guard: only ever delete a strict subdirectory of the segment root.
            if (!string.IsNullOrWhiteSpace(segDir))
            {
                var recScratch = Path.GetDirectoryName(segDir) ?? segDir;
                if (!string.Equals(Norm(recScratch), Norm(paths.SegmentDir), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(recScratch))
                    Directory.Delete(recScratch, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[Recordings] segment scratch cleanup failed for {Dir}", segDir);
            errors.Add($"capture scratch couldn't be removed ({ex.Message})");
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static string Mask(string? s) => string.IsNullOrEmpty(s) ? "" : "***";

    private static bool ValidEpgUrl(string? u)
        => string.IsNullOrWhiteSpace(u) || (Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == "http" || x.Scheme == "https"));

    private static RecordingPriority ParsePriority(string? p) => p?.ToLowerInvariant() switch
    {
        "cant_miss" or "cantmiss" => RecordingPriority.CantMiss,
        "opportunistic" => RecordingPriority.Opportunistic,
        _ => RecordingPriority.Normal,
    };
}

public sealed record CreateRecordingRequest(int ChannelId, long StartUtc, long EndUtc, int? PrePadS, int? PostPadS, string? Title, string? Priority, string? MatchQuery);
public sealed record ReassignRequest(int? SourceId, string? Priority);
public sealed record ImportAssignRequest(string? LeagueId, string? EventId);
public sealed record TestRecordingRequest(string Url, string? Name, int? Minutes);
public sealed record SourceUpsert(string? Label, string? Type, string? Protocol, string? Host, int? Port, string? Username, string? Password, string? EpgUrl, bool? EpgOverride, int? MaxStreams, bool? Enabled, string? UserAgent);
