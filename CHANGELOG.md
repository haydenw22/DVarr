# Changelog

All notable changes to **DVarr** (the DVR-first IPTV sports recorder). Newest first.

This project uses **Semantic Versioning** — `vMAJOR.MINOR.PATCH`:

- **MAJOR** — a generational leap / ground-up rework. We haven't hit one yet, so this stays **1**.
- **MINOR** — new functionality, or a major bug fix.
- **PATCH** — general updates, minor bug fixes, polish.

Dates are Brisbane (UTC+10). The version is reported on `/api/health` and comes from `<Version>` in `src/DVarr/DVarr.csproj` — bump it with every entry below. This log is backdated from the project's build history (`DECISIONS.md`) — internal same-night adversarial-review rounds are folded into the release they hardened rather than listed as separate versions.

---

## [1.23.0] — 2026-07-04
Smart auto-stop (no more missed extra time) · full UI redesign · league filters.

### Added
- **Smart auto-stop.** Recordings no longer cut off when a game runs long (extra time, penalties, red flags). Near the scheduled end, DVarr checks TheSportsDB: while the guide says the event is still in play (or gives no signal — motorsport), the recording **extends in 15-minute steps**, capped per league (default +60 min, motorsport +120 min) and never into the next recording's slot on the same login; once a terminal status (FT / AET / **AP — After Penalties**, the exact status of the Australia v Egypt game this missed) is reported, it closes after the normal post-pad. Auto never shortens a window. Per-league control in the league modal ("Recording stop": Auto [default] / Fixed + max extension); kill-switch `auto_stop_enabled`; every extension/close shows as an `AutoExtended` entry in the Activity feed. Migration Phase21.
- **League filters**: the Recordings page can filter by league (recordings now carry their league), and the Leagues page's channel-mappings table gets a league filter + aligned full-width layout.

### Changed
- **Complete UI redesign** — new deep-navy design language across every page: KPI stat cards, icon-chip panel cards with count pills and "View all" links, pill status badges, gradient primary buttons, redesigned sidebar (active-state accent bar, version chip) and topbar (slots + Database status chips), rebuilt dashboard (Recording Now / Scheduled 24h / Recently Completed / Sources / Next 24 Hours / Leagues panels), restyled tables, modals, tabs, guide and calendar.

### Docs
- `docs/12-remote-access-and-calendar.md` — full design (nothing built yet) for the subscribable calendar feed, external access at dvr.whittledigitalsolutions.com via SWAG/Cloudflare, and built-in accounts/roles with a simplified member UI. Includes a security finding: the IPTV stream-proxy redirect embeds provider credentials — external exposure stays blocked until auth ships.

## [1.22.0] — 2026-07-03
Guide-match channel picking, and no more silent "couldn't schedule" skips.

### Added
- **Records from the channel that actually shows the game.** Map two (or more) channels to a league — e.g. Fox Sports 503 + 504 for AFL — and within ~24 h of start DVarr re-checks each mapped channel's guide and moves the recording to the one whose EPG lists the event (with a final check right before recording starts). Same login only, with threshold + hysteresis so it never flaps; an `EpgRepick` entry appears in the Activity feed when it moves. If every mapped channel's guide is blank close to an event, DVarr quietly refreshes that source's EPG (at most once per 30 min). Toggle: Settings → Scheduling & EPG → *Guide-match channel pick*.
- **Loud warning when a monitored event can't be scheduled.** A league with monitored events but no usable channel mapping now raises an `Unresolvable` warning in the Activity feed (once per league per day) instead of a debug-only skip — the failure mode behind the missed West Coast v Adelaide game.
- Manually reassigned recordings are now **channel-locked** (new `Recording.ChannelLocked`, migration Phase20): the guide-match picker never moves a channel you chose by hand.

## [1.21.1] — 2026-07-02
Bug-audit fixes for v1.20.0–v1.21.0 (adversarial multi-agent review — 12 confirmed issues fixed, 4 false positives rejected).

### Fixed
- **Session-follow / per-session lengths are now hard-gated to motorsport everywhere** (scheduler, calendar, league create/edit, duration resolution). Any other sport's titles all classify as "Race", so an API-set session list on a team-sport league could have silently dropped every match — and worse, mass-cancelled its pending recordings. The UI never allowed this; the guard is defence-in-depth at every boundary.
- **A container redeploy no longer re-runs the daily EPG sync.** The fired-today marker is persisted (hidden `epg_auto_sync_last` setting), so only the first pass after the scheduled time syncs — not every deploy after 4am. Written before the sync starts so a mid-sync restart can't double-fire either.
- **EPG auto-sync robustness:** a fresh DI scope per source (a guide sync streams for minutes); a skipped/failed source now logs a *warning* (visible) instead of info; a corrupted timezone-offset value warns and falls back to Brisbane instead of silently meaning UTC; `epg_auto_sync_time` is validated as HH:MM at the API.
- **Calendar:** the response cap is applied *after* the follow filter (a heavily-filtered league can't eat the page budget); un-monitoring an event no longer force-shows it on a filtered calendar (a manual *monitor* still always shows).
- **League modal:** rapid league-switching can no longer let a stale response repaint the team/session pickers (latest-request-wins guard).

### Verified / rejected
- Four audit claims rejected as false positives: the "EPG fires twice per local day" claim (disproven by `DateTimeOffset.Date` semantics *and* the production log — one fire per Brisbane day); two "stale field when switching leagues mid-edit" claims (the override belongs to the league row being edited — by design); and the "empty open disclosure" claim (the disclosure holds the universal default-length field since v1.20.1).

## [1.21.0] — 2026-06-29
Scheduled automatic EPG refresh.

### Added
- **Auto-sync the TV guide on a daily schedule.** Settings → Scheduling & EPG now has **Auto-sync EPG daily** (on/off), an **EPG sync time** (time of day), and an **EPG sync timezone**. When on, DVarr refreshes every source's EPG once a day at that local time. A failed source keeps its previous guide (last-known-good); the manual per-source EPG button still works. Off by default. The timezone is a fixed UTC offset (no daylight-saving shift) — exact for Brisbane; the container intentionally ships no timezone database.

## [1.20.1] — 2026-06-29
Consistent per-league "Event length (advanced)" control for every sport.

### Changed
- The per-league length override now lives in an **"Event length (advanced)"** disclosure on the league modal for **all** sports (previously a plain inline field for team sports, while only motorsport had the expandable). For motorsport the same disclosure also holds the per-session overrides, so length settings are in one consistent place regardless of sport.

## [1.20.0] — 2026-06-29
Calendar follows your teams · motorsport session selection · tabbed Settings · per-session durations.

### Added
- **Following teams now declutters the calendar.** When a league follows specific teams, only those teams' games appear on the calendar — the full schedule is still synced behind the scenes so episode numbers stay correct. A manually-monitored event always shows.
- **Motorsport session selection.** Adding/editing a motorsport league (F1, V8…) now offers a session picker — Practice 1/2/3, Qualifying, Sprint Qualifying, Sprint, Race — populated from the sessions that league actually runs (V8 → just Race). New motorsport leagues default to **Race + Qualifying**; the scheduler arms (and the calendar shows) only the chosen sessions.
- **Per-session length overrides.** Set a different assumed length per session kind (e.g. Race 3 h, Practice 1 h) under "Session lengths (advanced)" in the league modal — for when the provider gives no end time.

### Changed
- **Settings is now tabbed** — Recording · Reliability · Scheduling & EPG · Data sources · Advanced — with fields laid out in columns/rows instead of one long scroll.

## [1.19.1] — 2026-06-29
Bug-audit fixes for v1.19.0 (adversarial multi-agent review — 12 real issues fixed, 1 false positive rejected).

### Fixed
- **Split-year leagues could skip a year of fixtures.** When a soccer-style `{year-1}-{year}` season matched, the follow-on season was derived from the wrong year (e.g. `2027-2028` instead of `2026-2027`), dropping months of games and skewing episode numbers. Now derived from the matched season. (Calendar-year sports — AFL, F1, World Cup — were unaffected.)
- **Team-follow robustness.** Event team ids are written authoritatively on every sync (a provider correction to "no teams" can't leave a stale id that mis-files a match); ids are trimmed consistently; and an event with no team ids at all is recorded rather than silently dropped (don't miss a not-yet-drawn final).
- **Narrowing a league's followed teams now cancels the dropped teams' pending recordings** instead of letting them still fire.
- **A partial league edit no longer wipes the TheSportsDB link.** Saving just the team list (or just the horizon) used to null `externalLeagueId`.
- **Clearer TheSportsDB diagnostics.** No key configured → a plain "set your key in Settings" message instead of doomed requests that looked like an outage; `401/403` → "authentication failed — check the key"; a `200` carrying an error body is no longer mistaken for data. The legacy v1 test key `3` is treated as "no key" (v2 rejects it), so an upgraded install fails loudly rather than silently.
- Hardened the league catalogue cache so a key change can't cross-pollinate cached results.

### Verified
- XSS-in-team-logo claim was investigated and **rejected** — `esc()` escapes quotes and HTML entities inside an attribute value don't re-delimit, so there's no breakout.

## [1.19.0] — 2026-06-29
Premium TheSportsDB v2 API: full-season sync, team-follow, and a refined League screen.

### Added
- **Follow specific teams in a league.** When adding/editing a team sport (AFL, NRL, soccer, rugby…), tick the teams you want — with their logos — and DVarr arms recordings only for those teams' matches. Tick none (or **All**) to record every match. Motorsport (no teams) is unaffected. The full schedule is still ingested, so episode numbers stay correct; only what gets recorded is filtered. Multiple teams per league supported.
- **Refined Add/Edit League modal.** Larger, with the league badge/logo header, a searchable picker across the **full** catalogue (AFL, NRL — the free key couldn't browse these), and a logo-rich team-follow picker.

### Changed
- **TheSportsDB v2 (premium key).** Migrated to the v2 API (`X-API-KEY` header). A league sync now pulls the **complete season in one call** instead of the old free-key day-sweep that silently dropped games (and made ~60 calls). Set your key in Settings → TheSportsDB API key.

### Fixed
- **League sync grabs every match.** The manual-import "Game" dropdown was missing fixtures because the old sync dropped games; it now lists the complete synced season (from local data).
- **Episode numbering.** With the complete season synced, a game's episode number is its correct chronological position in the year — e.g. AFL 11 Jun *Western Bulldogs v Adelaide Crows* = **E124**. The "weird AFL numbers" were the free key's incomplete data, not the numbering logic.

## [1.18.4] — 2026-06-29
Stop short labels wrapping mid-word.

### Fixed
- **Buttons, status pills and tags no longer break mid-word** ("EP/G", "Inges/t", "monitor/ed"). The dashboard's long-title wrap rule (`overflow-wrap`) was inheriting into these short labels; they now stay on one line.

## [1.18.3] — 2026-06-29
Dashboard fills the full width.

### Changed
- **Dashboard uses the full screen width again.** v1.18.2 capped it at 1600px, which left a wide empty band on an ultrawide monitor. The panels now stretch to fill the whole width while still capping at 3 columns (1 on a phone, 2 on a tablet, 3 on desktop). The service-worker cache version was bumped so the updated layout loads without a manual hard-refresh.

## [1.18.2] — 2026-06-29
Calmer dashboard layout.

### Changed
- **Dashboard is capped at 3 columns and a 1600px max width.** On wide / ultrawide / vertical monitors it no longer spreads its panels across a single long row — they now stack into a calmer, mostly-vertical layout (1 column on a phone, 2 on a tablet, up to 3 on desktop), kept to a comfortable reading width.

## [1.18.1] — 2026-06-29
Dashboard layout fix.

### Fixed
- **Dashboard no longer overlaps on wide / ultrawide / vertical monitors.** The *Recording now* and *Scheduled* panels reused the full multi-column Recordings table, which overflowed its narrow dashboard panel and spilled into the neighbouring panel. They now use compact flexbox rows (title + when + state, with a **stop** button on live recordings; tap a row for full controls on the Recordings page) that can never push wider than their panel. The panel grid also snaps to column counts that divide its six panels evenly (1 / 2 / 3 / 6), so rows stay balanced at every width instead of cramming skinny columns.

## [1.18.0] — 2026-06-29
Mobile/PWA + a full UI refresh, richer dashboard, and GPU-accelerated dead-feed detection.

### Added
- **Installable PWA.** DVarr can be added to a phone's home screen and launches standalone (no browser chrome) — a web manifest, icon set (generated from the logo), theme-colour, Apple touch-icon meta, and a service worker that caches the app shell for instant load over the VPN (live `/api/*` data, SSE, and previews are never cached). Ideal for checking on a recording from the couch or away from home.
- **Dashboard panels.** New **Recently completed** and **Sources** panels, plus an at-a-glance stat row (recording now / scheduled 24h / free slots / database). The Sources panel has one-tap **Refresh EPG** and **Ingest** so you can refresh the guide without leaving the dashboard.

### Changed
- **Responsive, mobile-first UI refresh.** Every view now reflows instead of "smushing" on a phone or a vertical monitor: the sidebar becomes a hamburger slide-out drawer, the dashboard is a fluid panel grid, the Recordings & Sources tables stack into cards on narrow screens, the guide's channel column narrows, and the calendar/topbar/modals adapt. Plus visible keyboard focus rings, smooth state transitions, larger touch targets, 16px inputs (no iOS zoom), reduced-motion support, and safe-area insets — all keeping the existing dark theme.

### Performance
- **Dead-feed detection now decodes on the GPU (NVDEC) and samples at 1 fps.** The black/freeze/silence check used to software-decode every frame of every active recording — the single biggest CPU draw (two recordings could peg the box). It now runs on the Nvidia GPU at ~1 fps, dropping that work to near zero while keeping the same detection. Tunable in Settings (`Dead-feed GPU decode`, `Dead-feed sample rate`); the recording itself is unchanged (still lossless `-c copy`).

### Fixed
- **Re-homing a recording to a different login no longer crashes.** `Recording.SourceId` is part of an alternate key (the structural guard that pins a recording's fallbacks to the same credential), so EF Core refused to change it on a tracked entity — which crashed **boot recovery** when a recording spread to another login, and lurked in the **reassign**, **re-Resolve** (per-recording and per-league), and **conflict-promotion** paths too (all five only failed when the source *actually* changed, which is why it surfaced now). They all re-point the credential through one helper that deletes the dependent fallbacks first, then applies the change with a tracker-bypassing UPDATE — also fixing a fallback-delete ordering bug in the conflict-promotion path. Verified by a standalone test that reproduces the original crash, confirms the fix, and proves the same-credential-fallback invariant still holds.
Settings page overhaul, per-league durations, a kickoff retry, and Threadfin removal.

### Added
- **Redesigned Settings page.** Settings are grouped (Recording, Reliability, Scheduling, Guide, TheSportsDB, Integrations, Display, Backups), each with a clear title + a one-sentence explainer. Booleans are real toggles, durations/intervals are number fields, URLs are URL fields, and the per-sport duration JSON is a validated textarea. Any unrecognised key still appears under **Advanced**, so a new setting is never hidden.
- **Per-league event-duration override.** Set an event length (in minutes) directly on a league when adding/editing it. Duration now resolves **league → sport → global default**, so one league (or a motorsport series) can run long without hand-editing JSON.
- **Retry at event start.** If a recording captures nothing during pre-roll (the channel isn't live yet), DVarr makes **one guaranteed fresh attempt at the event's real start time**. It never interrupts a recording that's already capturing, and it's toggleable in Settings.

### Removed
- **Threadfin.** The unused `threadfin_base_url` setting is gone and a stale DB row is auto-pruned on boot. DVarr has never used a proxy — streams and EPG are fetched directly from the provider.

## [1.16.0] — 2026-06-17
A re-Resolve action, plus a large reviewed reliability/correctness pass (29 fixes from a multi-agent audit, each independently verified).

### Added
- **Re-Resolve** — push a league's *current* channel mapping onto already-scheduled recordings **in place**, without delete-and-recreate. A per-recording button on Pending/Conflict recordings, and a per-league **Re-resolve** button that updates every scheduled recording for that league at once. Updates the channel/source/stream + the same-credential failover ladder; never touches a live capture; skips manual recordings.

### Fixed — reliability (High)
- **Tuner slot is freed as soon as capture ends**, before the (local-only) finalize — a long finalize no longer pins a login's only stream for up to an hour, so a back-to-back recording on the same credential can arm immediately instead of being missed.
- **Parked conflicts retime to the live event before arming** — a conflict whose fixture moved on re-sync no longer records the wrong window.
- **Postponed-then-rescheduled events record again** — a match cancelled by the postponement sweep is revived when it returns to the schedule (was blocked permanently).
- **No tuner-lease leak** when a cross-login spread fails mid-re-home; **boot reconcile no longer wipes a held slot** (which could let two streams run on one login).

### Fixed — correctness (Medium)
- EPG sync is **serialized per source** (concurrent syncs could duplicate or wipe a guide) and **keeps the last-known-good guide on truncation**; a degraded sync no longer wipes channel name-matches.
- TheSportsDB pull now **backs off on HTTP 429** (instead of silently dropping fixtures), **does not advance the sync timer on a failed pull** (retries instead of going stale for 6 h), **handles a not-yet-started competition**, and a **concurrent same-league sync can't lose the batch**.
- Re-resolve fallbacks write at the correct rank; the planner slot stays in sync after a retime (no same-tick double-book); `reassign` returns a real error instead of a false success; ffmpeg/ffprobe children are killed on timeout; manual-import staging no longer clobbers a same-named file; the UI no longer paints a stale page over a newer one and now surfaces league-edit errors.

### Fixed — hardening (Low)
- `AsNoTracking` on scheduler read-snapshots; empty channel names no longer wildcard-match in spread; EPG programme stop-times validated; manual-import nearest-match needs a tight window + title match; Plex `TitleMatch` precedence; resolve/reassign re-check enablement under the write gate; `PUT /api/settings` key allowlist + integer validation; `/api/sources` counts moved off the serialization thread; `FfmpegLocator` can't hang startup; live-refresh tolerates transient API errors.

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
