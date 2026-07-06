<p align="center">
  <img src="src/DVarr/wwwroot/dvarr-logo.png" alt="DVarr" width="96" />
</p>

<h1 align="center">DVarr</h1>

<p align="center"><b>Self-hosted IPTV sports DVR with one overriding job: never miss a can't-miss live sports event.</b></p>

<p align="center">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4" />
  <img alt="SQLite" src="https://img.shields.io/badge/SQLite-WAL-003B57" />
  <img alt="Docker" src="https://img.shields.io/badge/Docker-single%20container-2496ED" />
  <img alt="PWA" src="https://img.shields.io/badge/PWA-mobile%20ready-5A0FC8" />
</p>

![Dashboard](.github/media/dashboard.png)

DVarr watches the leagues you follow on [TheSportsDB](https://www.thesportsdb.com/), maps them to your IPTV channels, and records every event automatically — with a recorder built to survive feed drops, a scheduler that plans around your provider's stream limits, and guide intelligence that picks the right channel an hour before kick-off.

---

## Highlights

- **Bulletproof recorder** — supervised, segmented MPEG-TS capture (`-c copy`) that relaunches on stall and concatenates losslessly. A feed blip costs you the blip, not the event. GPU-accelerated dead-feed detection spots a channel stuck on a black/frozen slate and fails over to the next ranked channel.
- **League automation** — add a league once; events, posters and season data sync from TheSportsDB. Follow a whole league, just one team (only their fixtures are scheduled), or — for motorsport — exactly the sessions you care about (Qualifying + Race, skip the practice grind).
- **Sport-aware recording lengths** — an F1 practice, qualifying or sprint session books an hour; the race keeps a full three-hour window. Five resolution tiers mean a sensible default always exists and your override always wins.
- **Smart auto-stop** — near the scheduled end, DVarr polls the live score and *extends* the recording in 15-minute steps while the match is still in play. Extra time and penalty shoot-outs land in the file; a race ending is never trimmed.
- **Match-aware channel picking** — mappings are ranked fallbacks with optional pinning. An hour before start, DVarr re-checks the EPG (refreshing it if stale) and re-picks the channel actually showing your event, so a late programming change can't hijack the recording.
- **Guide + calendar** — a fast EPG grid (click a programme to schedule it), a monthly calendar of everything followed, and a token-secured **ICS feed** you can subscribe to from Google Calendar.
- **Mobile PWA** — install it on your phone; drawer navigation, card layouts and touch-sized controls, with zero functionality lost.
- **Login with trusted devices** — HTTP Basic for scripts and automations, a cookie login page for browsers (180-day trusted devices), credentials set via Docker env vars, and a rate-limited login endpoint. Machine-to-machine surfaces (Plex, Home Assistant, IPTV export, health) carry their own tokens and stay reachable.
- **Integrations** — Plex custom metadata provider, Sonarr-v3-compatible API for Prowlarr, Home Assistant status endpoint, credential-free M3U/XMLTV export for LAN IPTV players.

---

## Screenshots

### Leagues — follow a league, a team, or just the sessions you want

![Leagues](.github/media/leagues.png)

### Guide — click a programme to schedule it

![Guide](.github/media/guide.png)

### Calendar — every followed event at a glance (one-click ICS subscribe for Google/Apple Calendar)

![Calendar](.github/media/calendar.png)

### Plex — a built-in metadata provider, explained right in Settings

![Plex settings](.github/media/settings-plex.png)

### Recordings & Settings

| Recordings | Settings |
|---|---|
| ![Recordings](.github/media/recordings.png) | ![Settings](.github/media/settings.png) |

### Mobile PWA

| Dashboard | Drawer | Guide |
|---|---|---|
| ![Mobile dashboard](.github/media/mobile-dashboard.png) | ![Mobile drawer](.github/media/mobile-drawer.png) | ![Mobile guide](.github/media/mobile-guide.png) |

### Login — trusted devices stay signed in for 180 days

![Login](.github/media/login.png)

---

## Quick start (Docker Compose)

Requirements: Docker with Compose. That's it — the multi-stage [`Dockerfile`](Dockerfile) builds the .NET app and bundles ffmpeg.

```bash
git clone https://github.com/haydenw22/DVarr.git
cd DVarr
# edit docker-compose.yml volume paths, then:
docker compose up -d --build
# UI → http://<host>:1867   ·   health → http://<host>:1867/api/health
```

The repo's [`docker-compose.yml`](docker-compose.yml) is ready to use — set your three volume paths, your timezone, and real credentials in an untracked `.env` file:

```env
DVARR_AUTH_USER=yourname
DVARR_AUTH_PASS=a-long-random-password
```

| Mount | Purpose |
|---|---|
| `/config` | SQLite DB + settings — small; put it on a fast disk (SSD/NVMe) |
| `/media` | Finished recordings, filed in Plex-scannable show/season folders |
| `/segments` | In-flight capture scratch (several GB per recording), auto-cleaned after finalize — same filesystem as `/media` makes finalizing a cheap move |

> **Provider model:** typical IPTV providers allow **one stream per login**, so DVarr's concurrency comes from multiple credentials (one tuner slot each). Fallback channels are structurally restricted to the same login — the schema itself rejects a cross-credential fallback.

> **Unraid:** a Community Apps template is on the way. Until then, DVarr runs perfectly from Compose (or a manual Docker template) on Unraid — bind `/config` to appdata and `/media`/`/segments` to your array.

---

## Full setup guide

Five steps from empty container to fully automatic sports DVR: **log in → add your IPTV source → ingest channels + EPG → add a league → map it to channels.** Everything after that — event sync, scheduling, conflict planning, recording, filing for Plex — is automatic.

### 1 · First login

Browse to `http://<host>:1867`. Sign in with the credentials from your `.env` (defaults are `user` / `password` — the container log warns until you change them). Tick **Trust this device** and the cookie lasts 180 days.

### 2 · Get a TheSportsDB API key

DVarr's league catalogue and fixture sync come from [TheSportsDB](https://www.thesportsdb.com/). The built-in free test key only exposes a small sample of leagues, so a real key is effectively required:

1. Sign up at thesportsdb.com (a Patreon-supporter key unlocks the full v2 API).
2. In DVarr: **Settings → Data Sources → TheSportsDB API key** → paste the key → **Save**.

### 3 · Add your IPTV source

**Sources → Add source**, then fill in the details from your IPTV provider:

| Field | What to enter |
|---|---|
| **Label** | Any name, e.g. `My IPTV (login 1)` |
| **Type** | `xtream` for Xtream-codes providers (the common case), or `m3u` |
| **Protocol / Host / Port** | From your provider's URL, e.g. `http` · `provider.example.com` · `8080` |
| **Username / Password** | Your IPTV login |
| **Max streams** | How many concurrent streams this login allows (usually **1**) |
| **External EPG URL** *(optional)* | A full XMLTV URL (`https://…/epg.xml.gz`) if you prefer an external guide over the provider's built-in one — tick the override checkbox to make it win |
| **User-Agent** *(optional)* | Only if your provider requires a specific player UA |

Save, then on the source's row click:

- **Ingest** — pulls the full channel list (uses one stream slot briefly). Channels land on the **Channels** page, filterable by provider group.
- **EPG** — pulls the TV guide (from the provider's XMLTV, or your external URL). The toast reports how many programmes matched how many channels.

Have multiple logins with the same provider? Add each as its **own source** — each becomes a tuner slot, and DVarr spreads simultaneous recordings across them.

### 4 · Keep the EPG fresh (recommended)

**Settings → Scheduling & EPG**:

- **Auto-sync enabled** — refreshes every source's guide once a day.
- **EPG sync time / timezone** — pick a quiet hour in your local timezone.
- **EPG re-pick** (on by default) — within ~1 hour of an event, DVarr re-checks each mapped channel's guide and records from the channel *actually showing the event*.

### 5 · Add a league

**Leagues → Add league**:

1. Pick a **Sport**, then find your **League** with the search box (or paste a TheSportsDB league id for anything unlisted).
2. Choose what to follow:
   - **Team sports** — a *Teams to follow* list appears. Tick your team(s) to record only their fixtures; tick none (or all) to record every match.
   - **Motorsport** — a *Sessions to record* list appears (Practice / Qualifying / Sprint / Race). Follow just Quali + Race if that's your thing, with optional per-session length overrides.
3. Recording behaviour (the defaults are sensible):
   - **Recording stop** — *Auto* extends the recording in 15-minute steps while TheSportsDB still shows the event live (up to **Max extension**); *Fixed window* records exactly the scheduled slot.
   - **Auto-schedule horizon** — how many days ahead DVarr plans recordings (default 14).
   - **Calendar colour** — the league's colour on the Calendar page.
   - **Monitored** — leave ticked for automatic recording.
4. Save, then hit **Sync** on the league's row to pull its fixtures immediately.

### 6 · Map the league to channels

Recording needs to know *where* the league airs. On the league's row click **Map**:

1. Pick **Source → Group → Channel** (the channel box is a keyword search — type `bein`, `fox 503`, whatever matches).
2. **Rank** — 1 is the first choice; add more mappings at rank 2, 3… as fallbacks. If rank 1 won't open or dies mid-event, DVarr fails over down the list automatically.
3. **Pinned** — your pick beats EPG title-guessing. Leave it on unless you want DVarr free to reorder by guide match.

All fallbacks for a league must be on the **same provider login** — one stream per login means a mid-recording failover can't jump credentials (the schema enforces this).

**That's the setup done.** The dashboard shows what's planned; the Calendar shows every followed event; recordings start, survive drops, auto-extend, then get filed into `/media` with posters and `.nfo` metadata, ready for Plex.

### 7 · Optional extras

- **Plex** — DVarr ships a Plex Custom Metadata Provider (Plex 1.43+). In Plex: **Settings → Metadata Agents → Add Provider** → `http://<dvarr-lan-ip>:1867/plex` → restart Plex → point a **TV Shows** library at the DVarr agent. Real game titles and TheSportsDB artwork on every recording. Full instructions live in **DVarr → Settings → Plex**.
- **Calendar subscription** — Calendar page → **Subscribe (ICS)** for a token-secured feed Google/Apple Calendar can poll. Set **Settings → Data Sources → Public base URL** if you access DVarr through a reverse proxy.
- **Home Assistant** — set a webhook URL under **Settings → Data Sources** to push recording state changes; a status endpoint is also available for polling.
- **IPTV players on your LAN** — credential-free M3U + XMLTV export endpoints let players tune channels without ever seeing your provider login.
- **Live preview GPU accel** — recording never needs a GPU, but the in-browser live preview transcodes; on an NVIDIA box enable the commented `runtime: nvidia` block in the compose file and set **Settings → Reliability → content_verify_hwaccel** to `cuda` for GPU dead-feed checks too.
- **Continuous DB backup** — point **Settings → Data Sources → litestream_target** at an S3-compatible bucket.

---

## How recording lengths are resolved

Every event gets its window from the first tier that answers — so a sensible default always exists, and anything you set explicitly always wins:

| Tier | Source | Example |
|---|---|---|
| 0 | **Your per-session map** on the league (motorsport) | "Sprint = 90 min, everything else default" |
| 1 | **Per-league override** | "Everything in this league is 2.5 h" |
| 2 | **Built-in motorsport session defaults** | Practice / Qualifying / Sprint → **1 h** · Race / Testing → **3 h** |
| 3 | **Per-sport defaults** | Fighting 5 h · Golf 6 h · Cricket & Tennis 4 h · Motorsport 3 h |
| 4 | **Global default** | 2 h |

Two safety nets sit under all of this: every recording carries post-padding, and **smart auto-stop** keeps extending a live recording past its scheduled end while the guide still shows it in play — so a tight default can't truncate a session that runs long.

---

## Local development

```powershell
dotnet build src/DVarr/DVarr.csproj
dotnet run --project src/DVarr/DVarr.csproj
# → http://localhost:1867  (login: user / password unless env vars are set)
```

On Windows, runtime data (SQLite DB, segments) goes to `src/DVarr/bin/Debug/net8.0/_localdata/` so it runs without the Linux `/config` mounts. The **Quick test recording** card records a public test stream — the provider is never contacted unless you explicitly ingest a source.

Migrations (applied automatically on startup):

```powershell
dotnet ef migrations add <Name> --project src/DVarr/DVarr.csproj --output-dir Data/Migrations
```

---

## Architecture

- **Stack:** .NET 8 (ASP.NET Core minimal APIs), EF Core + SQLite (WAL, single serialized writer), vanilla-JS SPA, one Docker container, ffmpeg for capture/concat/preview (NVDEC/NVENC when a GPU is present).

```
src/DVarr/
  Program.cs                  # host, DI, startup migrate + seed
  Data/                       # DbContext, entities, EF migrations, enum→TEXT mapping
  Infrastructure/             # epoch-UTC time, WAL pragmas, single-writer gate, auth middleware
  Services/
    Recording/                # segmented recorder + supervisor, smart auto-stop, live preview
    Scheduling/               # durable scheduler (arm/resume/missed)
    Tuner/                    # per-credential tuner-lease pool, cross-login spreading
    Ingest/                   # Xtream channel ingest + XMLTV EPG ingest
    Events/                   # TheSportsDB sync, auto-scheduler, conflict planner,
                              #   channel resolver + EPG re-pick, session classifier
    Media/                    # finished-file import: .nfo, artwork, Sonarr/Plex-style folders
  Api/                        # REST + SSE endpoints, calendar feed, auth, Plex/Sonarr/HA parity
  wwwroot/                    # SPA (PWA: manifest + service worker)
Dockerfile                    # multi-stage build + ffmpeg runtime
```

**Invariants the design enforces:**

- Every stored/wire time is a **UTC epoch second** — a naive local datetime is unrepresentable, so timezone double-conversion bugs can't exist.
- A **cross-credential fallback is unrepresentable** — a composite foreign key ties every fallback to the primary's provider login.
- **One writer, WAL, `busy_timeout`** — "database is locked" storms can't recur.
- Recordings are **never orphaned by a re-sync** — events upsert by a stable natural key; nothing is deleted or re-keyed.

See [`CHANGELOG.md`](CHANGELOG.md) for the full per-version history.
