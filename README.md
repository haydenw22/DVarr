# DVarr

Reliability-first, self-hosted **IPTV sports DVR** — the build of the [Sportarr Replacement plan](../README.md). Its one overriding job: **never miss a can't-miss live sports event.**

- **Stack:** .NET 8 (ASP.NET Core), EF Core + SQLite (WAL, single writer), single Docker container.
- **Recorder (the point):** supervised, segmented MPEG-TS capture that relaunches on stall and concatenates losslessly — a feed blip costs the blip, not the event. Default `-c copy`.
- **Provider model:** the IPTV provider allows **one stream per login**, so concurrency comes from **multiple credentials** (one tuner slot each). Same-credential fallback is enforced structurally.

Design docs live in [`../docs/`](../docs/); this README covers building and running the code.

## Status

| Phase | State |
|---|---|
| **0 — Skeleton + multi-source foundation** | **Done.** Host + DI, SQLite WAL + single-writer gate, full data model + migrations, settings, `/api/health`, Dockerfile + Unraid template, Xtream client + per-source channel ingest (gated), multi-source seeding, source-toggle API. |
| **1 — Manual bulletproof DVR** | **Done.** Segmented recorder + supervisor relaunch + lossless concat (PTS de-overlap), per-credential tuner-lease pool + cross-login spreading, durable scheduler (arm/resume/missed), SSE live status, web UI. |
| **2 — Event data + EPG + resolver** | **Done.** Full XMLTV EPG ingest + name-matching, churn-proof events, anti-hijack resolver, credit-aware conflict planner, auto-scheduler. |
| **3–5** | **Done.** Media import (`.nfo`/artwork, Sportarr-style folders) + manual Import flow, Sonarr-v3/HA/credential-free export parity, Plex metadata provider, full SPA. |

See [`CHANGELOG.md`](CHANGELOG.md) for the per-version history (current **v1.14.0**); [`../docs/10-roadmap-and-phases.md`](../docs/10-roadmap-and-phases.md) is the original sequencing plan.

### Run it
```powershell
dotnet run --project src/DVarr/DVarr.csproj   # → http://localhost:1867
```
Open the UI, use the **Quick test recording** card (prefilled public stream — no provider contact) and watch a capture run through the state machine to a finished `.mkv`. The provider is **not** contacted unless you press **“ingest channels”** on a source.

## Prerequisites

- .NET 8 SDK (installed on this workstation: `8.0.422`).
- `dotnet-ef` global tool (`dotnet tool install --global dotnet-ef --version 8.0.10`) for migrations.
- Docker on the Unraid server for image builds (no local Docker needed).

## Build & run locally (Windows dev)

```powershell
# from the DVarr/ folder
dotnet build src/DVarr/DVarr.csproj
dotnet run --project src/DVarr/DVarr.csproj
# → http://localhost:1867/  and  http://localhost:1867/api/health
```

On Windows, runtime data (SQLite DB, segments) goes to `src/DVarr/bin/Debug/net8.0/_localdata/` so it runs without the Linux `/config` mounts. In the container these are the mounted volumes `/config`, `/media`, `/segments`.

## Database migrations

```powershell
dotnet ef migrations add <Name> --project src/DVarr/DVarr.csproj --output-dir Data/Migrations
```

Migrations are applied automatically on startup (`db.Database.MigrateAsync()`). A design-time factory (`DVarrDbContextFactory`) lets the EF tooling build the context without running `Program.cs`.

## Docker build & deploy (Unraid)

The image compiles inside the SDK stage, so the server needs only Docker:

```bash
# on Whittle-Server, from a synced copy of this folder
docker build -t dvarr:latest .
# then add the container via the Unraid template at deploy/dvarr.xml
# (volumes: /config -> appdata, /media -> Sports library, /segments -> SSD cache if available; port 1867)
```

Inter-container references use `192.168.4.63:<port>` — never `localhost` or `172.17.x.x` (the bridge IP that caused Sportarr's duplicate-indexer storms).

## Project layout

```
src/DVarr/
  Program.cs                 # host, DI, path resolution, startup migrate + seed
  Data/
    DVarrDbContext.cs        # DbSets, enum→TEXT, indexes, same-credential composite FK (bug #7)
    DVarrDbContextFactory.cs # design-time factory for `dotnet ef`
    Enums.cs                 # RecordingState (canonical FSM) etc.
    Entities/                # Provider, Catalog, Events, Recordings, Ops
    Migrations/              # EF migrations
  Infrastructure/
    EpochTime.cs             # UTC epoch storage; Brisbane (+10, no DST) at display only
    SqlitePragmaInterceptor.cs # WAL + busy_timeout + foreign_keys per connection
    DbWriteGate.cs           # single serialized writer
  Services/SettingsService.cs# typed config with canonical defaults
  Api/HealthEndpoints.cs     # GET /api/health
deploy/dvarr.xml             # Unraid Community Apps template
Dockerfile                   # multi-stage build + ffmpeg runtime
```

## Key invariants already enforced

- **Every stored/wire time is a UTC epoch second** (no naive datetime is storable) — the structural fix for the +10h Brisbane bug.
- **A cross-credential fallback is unrepresentable** — `RecordingFallback (RecordingId, SourceId)` is a composite FK to `Recording (Id, SourceId)`, so the DB rejects a fallback on a different login.
- **One writer, WAL, `busy_timeout=15000`** — the "database is locked" storms cannot recur.
