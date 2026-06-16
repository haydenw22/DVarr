# Changelog

All notable changes to **DVarr** (the DVR-first IPTV sports recorder). Newest first.

This project uses **Semantic Versioning** — `vMAJOR.MINOR.PATCH`:

- **MAJOR** — a generational leap / ground-up rework. We haven't hit one yet, so this stays **1**.
- **MINOR** — new functionality, or a major bug fix.
- **PATCH** — general updates, minor bug fixes, polish.

Dates are Brisbane (UTC+10). The version is reported on `/api/health` and comes from `<Version>` in `src/DVarr/DVarr.csproj` — bump it with every entry below. This log is backdated from the project's build history (`DECISIONS.md`) — internal same-night adversarial-review rounds are folded into the release they hardened rather than listed as separate versions.

---

## [1.15.0] — 2026-06-17
Correct, tournament-accurate episode numbering, plus a batch of reviewed reliability fixes.

### Fixed
- **Episode numbers are now the true tournament game number** (e.g. the 8th match of the World Cup = `E08`), so Plex matches each recording to the right game name + thumbnail. The cause was two-fold: the manual **Import** flow numbered by day-of-year (Spain v Cape Verde came out `E167`), and — more fundamentally — event ingestion only saw a partial schedule, so even chronological numbering was wrong.
- **Manual Import now links the recording to its local event** and numbers it through the exact same path as auto-import and the Plex agent — no more day-of-year. If the league isn't tracked locally it falls back to a chronological season position rather than day-of-year.
- **Event ingestion now pulls the *complete* backdated schedule.** TheSportsDB's free tier caps the season endpoint at ~5 matches and its per-day endpoint silently *drops* games (confirmed: Australia v Turkey, Sweden v Tunisia, Netherlands v Japan vanished from the day feed). Ingestion now sweeps per-day from the competition's first match through the horizon **and gap-fills missing fixtures by `lookupevent`** (the per-id endpoint hits the full DB), so the local schedule — and therefore every episode number — is complete and stable. Decoupled from the arming horizon; past games are never re-recorded.

### Fixed (reviewed batch — two adversarial review rounds)
- **EPG sync is now last-known-good** — a failed/partial guide refresh keeps the previous guide instead of blanking it.
- **Recording start is atomic** (no double-start race); **stop/delete waits for finalize** and reports `finalizing` (409) rather than deleting underneath a running capture.
- **Stale event-status sweep** cancels Pending/Conflict recordings when an event is Cancelled/Postponed (never on Completed — full race endings kept); **event/league delete** also cancels `Conflict` recordings, not just `Pending`.
- **Per-source `User-Agent`** is wired end-to-end (seed → CRUD → UI); **duplicate source labels / league TheSportsDB ids return a clean 409**; manual recordings on a disabled source are rejected up-front.
- **HA `free_credentials`** counts only enabled sources; the Sonarr-emulation API key is logged only when first generated; episode `.nfo` `uniqueid` includes the season/year; reverted an over-aggressive unique channel index.

## [1.14.0] — 2026-06-16
Manual-recording staging + an Import/assign flow.

### Added
- **Manual recordings no longer land loose in the Sports library.** An unmatched manual recording is now parked in a Plex-ignored **`<media>/.unsorted/`** folder (Plex skips dot-prefixed dirs), instead of dropping into the library root where Plex would scan it.
- **Import button** on finished, unsorted recordings → a **Sport → League → Game** picker (`/api/import/events` lists that league's fixtures around the recording's date). Choosing the game re-files it into the proper `League/Season/<Title> (date) ENN/… - HDTV-<height>p.mkv` layout with `.nfo` + artwork, and moves it out of `.unsorted` so Plex picks it up. Backed by `POST /api/recordings/{id}/import` + `TheSportsDbClient.GetEventByIdAsync`. (Event-linked and auto-matched recordings still file automatically as before.)


Conflict-planning + stop bug fixes, plus a manual "Start now" control.

### Added
- **Start button** on Pending/Conflict recordings (UI + `POST /api/recordings/{id}/start`) — force-arm a recording early/manually, before its pre-roll. Uses the recording's own window end and benefits from cross-login spreading if the credential's busy.
- **`Cancelled` state** — stopping a recording before it ever captured now marks it `Cancelled` (was an inaccurate `Done`); the activity feed logs a cancellation.

### Fixed
- **Overlapping recordings now actually spread to the second login.** Manual recordings were pinned to the chosen source with no fallbacks, and the scheduler left a busy-credential recording in Pending until it Missed — so the schedule modal's "will record on \<other login\>" badge never happened. The recorder now re-homes a recording to the same channel on a free login at arm time (matching by stream id / logical key / name).
- **Stop now works on every recording.** The stop endpoint only handled *active* and *Pending* states; a `Conflict` recording (or an orphaned state after a restart) silently no-op'd, and nothing was logged. It now cancels any non-terminal recording, finalizes a live one to Done, and logs every stop attempt with the state + whether it was actively capturing.
- **Native-rate (VOD/test/DirectUrl) recordings no longer loop on clean EOF** — the Phase 1.12.0 clean-EOF instant-relaunch is now live-only; a finite input that ends is finalized instead of restarted.



### Added
- **Conflict planning across the two provider logins.** New `CreditAwarePlanner` spreads overlapping events onto the second login instead of wasting it; when both logins are full an event is parked in a new **Conflict** state (re-promoted automatically when a slot frees). New **Conflicts** page (per-credential load + reasons, reassign / bump-priority actions) and a live "where will this land?" badge in the Schedule modal. New `League.Priority` tie-breaker. Endpoints: `/api/conflicts`, `/api/recordings/plan-preview`, `/api/recordings/{id}/reassign`.
- **Per-sport recording durations** for events with no provider end-time: `default_event_duration_s` = 2h, `event_duration_overrides_json` = `{"motorsport":10800}` (race endings stay long). World Cup matches now block ~2h + post-pad instead of 3h.
- **Sportarr-style finished-game filing**: per-game folder `<Title> (yyyy-MM-dd) E<NN>/` plus an `HDTV-<height>p` resolution tag, e.g. `FIFA World Cup/Season 2026/USA vs Paraguay (2026-06-12) E04/FIFA World Cup - S2026E04 - USA vs Paraguay - HDTV-2160p.mkv`.
- **App version** is now surfaced on `/api/health` (SemVer from the csproj).

### Changed
- **Container ffmpeg upgraded 5.1.9 → 8.1.1** (BtbN static GPL build, NVENC intact) — materially more robust on reconnects and corrupt 4K HEVC streams.
- **Capture timeline is now continuous** (`-copyts`, dropped `-reset_timestamps`) so reconnect duplicates are detectable; finalize audio gains `aresample=async` + `-avoid_negative_ts make_zero`.

### Fixed
- **Recordings "going back in time."** On a flaky line the provider re-served buffered seconds on reconnect, and the old re-zeroed segments concatenated them verbatim. Finalize now runs a lossless **de-overlap pass** (drops duplicate segments, trims partial overlaps via `inpoint`). Gated by `finalize_deoverlap_enabled` (default on).
- **Recovering "spam" on momentary drops.** A clean end-of-stream now relaunches instantly and stays in *Recording* (flap-throttled), instead of cycling through Recovering. Gated by `clean_eof_instant_relaunch` (default on).
- **Episode-number collisions** (every World Cup matchday-1 game came out `E01`): the manual-match path no longer uses the provider round number; the event-linked path keeps a stable chronological per-season ordinal.
- **Schedule modal overflow** with long channel names — fields/controls are width-capped and the channel results are now an ellipsis-truncated listbox (full name on hover).

---

## [1.11.0] — 2026-06-16
- **NVENC for the live-preview transcode.** HEVC/AC-3 channels that fall back to a server-side transcode now use the GTX 1070 (`h264_nvenc`, NVDEC, `scale_cuda`) instead of software libx264 — container CPU during a preview dropped from pegging cores to ~3%, and it runs concurrently with Plex's NVENC. Per-session libx264 fallback retained.

## [1.10.0] — 2026-06-16
- **Fixed event sync only returning ~5 events.** TheSportsDB tightened their free-tier `eventsseason.php` to a season's first few matches. `EventFetcher` now also fetches `eventsday.php` per day across each league's horizon (uncapped on the free key), merged by stable event id. World Cup re-sync went 5 → 49 events. (Not a DVarr bug — a provider change — but DVarr now works around it.)

## [1.9.0] — 2026-06-16
- **Deployed to Unraid as a Docker container**, replacing Sportarr — `dvarr:latest` at `192.168.4.63:1867`, volumes mapped (`/config`→appdata, `/media`→Media/Sports, `/segments`→array scratch), Unraid dashboard logo + WebUI link.
- **Auto-cleanup of recording segment scratch** after a recording finishes (the 6 GB+/recording temp files were never being removed).
- Fixed the container health-check (the base image shipped no wget/curl).

## [1.8.0] — 2026-06-15
- **Major recording-reliability fix.** A corrupt source PTS spike (`+igndts` trusting a bad timestamp) was inflating finished files to bogus 25-hour durations, causing freezing/desync ("frame rate deteriorating") and apparent audio loss. Capture now self-heals a rogue PTS from the good DTS (`setts` bitstream filter, still lossless `-c copy`), and finalize re-encodes to one uniform AAC track so a mid-stream audio-codec change can't leave a silent splice. The affected FIFA recording was salvaged losslessly.

## [1.7.0] — 2026-06-15
- **Content verification engine** (opt-in, off by default): a decode-only second output on the same connection runs black/freeze/silence detection and routes a sustained dead picture into the failover ladder (`content_verify_enabled`, `content_dead_timeout_s`).
- **Coherent 1-based fallback ranks** (rank 1 = primary, 2…N = same-login fallbacks).
- Dashboard restored to a 2×2 grid; Leagues page mapping/rank documentation.

## [1.6.1] — 2026-06-15
- Guide click-to-schedule now correctly prefills the group + channel (the group list had been truncated).
- Guide recording highlight split: core window red, pre/post-roll buffer orange.
- Dashboard decluttered to Scheduled + Next-24-hours.

## [1.6.0] — 2026-06-15
- **Curated TheSportsDB league catalog** (~48 leagues incl. AFL, F1, V8 Supercars, WEC, World Cup, NRL, A-League, NBA/NFL/MLB/NHL) + "paste a league id" — works on the free key, no Premium needed.
- **Name-based EPG matching** (TiviMate-style): channels without a provider tvg-id now match the guide by normalised display-name — EPG coverage jumped from a handful to ~all sports channels.
- Dashboard rebuilt (at-a-glance recording/scheduled/upcoming + league posters); smoother live-preview buffering; clearer calendar colours; wider guide channel column.

## [1.5.0] — 2026-06-15
- **Live preview fixed** (the Web-Worker relative-URL bug that broke every preview) + a **server-side HEVC/AC-3 → 720p H.264 HLS transcode fallback** for browsers that can't decode 4K HEVC.
- **Guide fixed**: case-insensitive EPG matching, EPG-bearing channels first, defaults to the source that has a guide; 6h/12h/24h views.
- **Calendar rebuilt as a monthly grid** with a per-league colour picker.
- Source 1 re-enabled (off-limits guard retained as the mechanism).

## [1.4.0] — 2026-06-15
- **Schedule modal → cascading Source → Group → Channel** with keyword search + a "match to TheSportsDB & rename for Plex" option.
- **In-browser live preview** via a credential-safe byte-proxy (leases the login's single slot).
- **Guide rebuilt as a real timeline** (now-playhead, live = green, recording = red, click-to-schedule).
- **EPG stores the entire external guide** (decoupled from the channel list; configurable multi-million-programme cap, streamed in batches).
- **Leagues are TheSportsDB-backed** (sport → searchable league, posters + events auto-sync).
- **Plex Custom Metadata Provider** at `/plex` (league = show, year = season, event = episode, with artwork).

## [1.3.0] — 2026-06-15
- **Event data + anti-hijack resolver** ("Sportarr-like" layer): leagues, churn-proof event upsert by natural key, pinned league→channel mapping with same-credential fallbacks, and a background auto-scheduler that creates Pending recordings within each league's horizon.
- **Parity surfaces**: credential-free `filtered.m3u`/`.xml` exports (login never in the file), a minimal Sonarr-v3 API for Prowlarr, and Home Assistant status + webhook.
- **Media import** into a Plex/Jellyfin-clean library (`.nfo` sidecars + thumbnails), with path-traversal containment.

## [1.2.0] — 2026-06-15
- **Channel group filter** (source-aware) on the Channels page.
- **External EPG per source** (XMLTV `.xml`/`.xml.gz`, override checkbox) as a separate sync action.
- **Source CRUD** (add/edit/delete; credentials write-only/masked; delete blocked while recording).
- Recording segments recorded to the DB; reliability hardening (finalize always runs; tuner-slot leak fixed; boot re-finalizes surviving segments).

## [1.1.0] — 2026-06-14
- **Recording engine + web UI** — the first functional DVR: Xtream ingest, one-stream-per-login tuner leasing, supervised segmented MPEG-TS capture with relaunch-on-stall and lossless concat at finalize, the durable scheduler, live SSE updates, the full REST API, and the multi-page sidebar UI (Dashboard, Recordings, Channels, Guide, Sources, Activity, Settings). Verified end-to-end with a real test recording.

## [1.0.0] — 2026-06-14
- **Project foundation.** .NET 8 host + DI, SQLite (WAL) with a single-writer gate, the full data model + initial EF migrations, the typed Settings store, `/api/health`, and the Docker/Unraid packaging. Structural fixes baked in from day one: epoch-seconds timestamps, the canonical recording state machine, and a database constraint that makes a cross-login fallback impossible to represent.
