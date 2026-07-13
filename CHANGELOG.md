# Changelog

All notable changes to **DVarr** (the DVR-first IPTV sports recorder). Newest first.

This project uses **Semantic Versioning** — `vMAJOR.MINOR.PATCH`:

- **MAJOR** — a generational leap / ground-up rework. We haven't hit one yet, so this stays **1**.
- **MINOR** — new functionality, or a major bug fix.
- **PATCH** — general updates, minor bug fixes, polish.

Dates are Brisbane (UTC+10). The version is reported on `/api/health` and comes from `<Version>` in `src/DVarr/DVarr.csproj` — bump it with every entry below. This log is backdated from the project's internal build history — internal same-night adversarial-review rounds are folded into the release they hardened rather than listed as separate versions.

---

## [1.38.1] — 2026-07-14
Placeholder-stream detection, an enforced recording cap, a Discord link, and a Settings tidy-up.

### Added
- **Bitrate-floor placeholder detection.** Some providers keep a "channel offline" slate playing at a trickle of data — the stream stays technically alive so it never trips a stall, but nothing real is recording. DVarr can now spot this from the bitrate alone (no GPU needed, unlike the picture-based dead-feed check): turn on **Settings → Reliability → Bitrate-floor placeholder detection** and set a floor per quality tier — **SD**, **HD** (720p/1080p) and **4K** (2160p). If a channel's stream stays below its tier's floor for a sustained window, DVarr treats it as a placeholder and fails over down the same ladder as a dead feed. Opt-in and off by default; an unclassified channel uses the SD floor so genuinely low-bitrate streams aren't wrongly dropped.
- **A Discord link** in the sidebar, next to the Ko-fi button — [join the DVarr community](https://discord.gg/Nb59pEzGb6).

### Changed
- **The "Max simultaneous recordings" setting is now enforced.** DVarr won't start more than that many recordings at once across all logins; an event that would exceed the cap waits and starts when a slot frees, rather than the setting being ignored.
- **Settings tidy-up:** removed three settings that never did anything (recorder input mode, dead-feed check interval, default channel filter) and the Litestream backup field, which was never implemented — its presence implied a backup was running when none was. (Startup already snapshots the database before applying any migration.)

## [1.38.0] — 2026-07-14
The Library: DVarr now tracks what's physically on the drive. Finished recordings graduate off the Recordings page into a Plex-style Library, and everything — including a recording still in progress — can be watched in the browser.

### Added
- **Library page** (GitHub follow-up on the delete-files issue: *"there should be a way to see and delete what's still physically on the drive"*). A new **Library** tab shows every recorded file organised exactly as Plex/Jellyfin see it — **league → season → game** — with episode tag, air date, quality, duration, size and the channel/source it was captured from, plus per-league size rollups, library totals and **free disk space**. A **finished recording no longer sits as "Done" on the Recordings page — it appears here instead** (Recordings stays the request/capture pipeline, with a "finished → Library" link). Searchable by league, team or game name.
- **The library is tracked, not guessed.** Files are registered the moment a recording is filed, in a store that survives everything around it being deleted: remove the recording entry (keeping the file) and the library still knows exactly what the file is. A **reconciling disk scan** (startup, every 6 hours, and a **Rescan disk** button) keeps it honest: it **adopts files DVarr didn't create** ("found on disk" badge — league/season/game reconstructed from the file layout and matched to your leagues/events), **marks files removed outside DVarr as Missing** (healing automatically if they come back, and recognising an externally moved/renamed file by name+size so it keeps its history), and refuses to touch anything when the media share is simply unmounted — a flaky mount can never mass-flag the library. Existing installs backfill their finished recordings into the library on first boot.
- **Watch from the Library.** Every file plays in the browser: H.264 recordings are remuxed on the fly (lossless — the full seekable timeline appears within seconds), HEVC/other codecs are transcoded (NVENC on the server GPU, CPU fallback). Idle playback sessions clean themselves up.
- **Watch a recording while it's still recording — without using a second provider stream.** The ▶ **watch** button on a live recording plays from the capture segments already on disk, so it can never fight the recording (or anything else) for the credential's single slot. The timeline keeps growing while it records — seek anywhere in what's been captured, jump to the live edge — and rolls seamlessly into normal playback when the recording finishes. Failed recordings get a **footage** button that plays whatever was captured before the failure.
- **Delete from the Library deletes the actual file**, its artwork/NFO sidecars, and any game/season/league folders the delete emptied (never the media root), with the file size stated in the confirm dialog. A Missing entry's delete just removes the entry. Deleting a recording with "also delete the file" keeps the library in sync automatically; deleting a library file never touches the scheduler's history, so the event can't accidentally re-record.
- **Import from the Library.** Unsorted files (manual captures awaiting a match, or adopted strays) are grouped at the top with the same sport → league → game Import dialog — which now also works for files whose original recording entry is long gone.
- Dashboard's "Recently completed" panel is now **"Recently finished"**: files that landed in the library merged with any failures, each linking to the right page.

### Changed
- `/api/recordings` no longer returns Done rows (they live at the new `/api/library`); the Done option is gone from the Recordings page filter. Done rows are kept internally as scheduler history, so nothing re-records.

## [1.37.2] — 2026-07-13
UI hardening batch: fifteen interface and accessibility fixes from the full-application audit, verified against a live instance.

### Fixed
- **The Leagues page can no longer submit a different league than the one it's showing.** Typing in the league search box could leave the visible header on one league while a different one was submitted; the picker now keeps your chosen league selected (even when a filter scrolls it out of view) so the header and the saved league always match.
- **The Map dialog can no longer map channels to the wrong league or silently drop a team scope.** The channel selection is reset when the dialog opens, "Add all" stays disabled until the picker is ready, and a mapping started from "+ Add team" keeps that team even if you submit before the team list finishes loading.
- **Mapping and league dialogs stay open until the save actually succeeds** — a rejected save no longer discards everything you entered behind a bare error toast.
- **Channel priority is now reorderable by keyboard and touch**, not just mouse drag — each channel has ▲▼ buttons alongside the drag grip. A cancelled drag (Esc, or dropping outside the list) now snaps back instead of leaving an unsaved reordering on screen.
- **A failed page load looks like a failure, not an empty account** — the Leagues page shows an error with a Retry button instead of "No leagues yet" when the data couldn't be fetched.
- **Bulk actions on Recordings only affect visible rows** — a recording selected under one filter is no longer silently acted on after a filter hides it.
- **Stale background updates can no longer repaint the wrong page.** Live refreshes and slow filter/search responses are tied to the current page and query, so navigating quickly or changing a filter can't leave older results on screen.
- **The first paint uses your configured timezone** instead of briefly flashing the default and rendering twice.
- **Opening the mobile menu and then widening to desktop no longer leaves the page unable to scroll.**
- **Accessibility:** dialogs are proper labelled modals that trap keyboard focus and restore it on close; the channel and game pickers are keyboard-navigable (arrow keys, Home/End, Enter/Space); status messages announce to screen readers; and the guide's channel-name tap behaviour works as intended on phones.

## [1.37.1] — 2026-07-13
Integrity batch: nineteen fixes from a full-application audit, every finding verified against the code before fixing.

### Fixed
- **Deleting an active recording now deletes the real file.** The delete endpoint snapshotted the file path from a stale entity read before finalize + media import had moved the file, so "also delete the file" silently orphaned the imported MKV. The path is now re-read fresh after the stop settles. Cleanup failures are also reported in the delete response and toast instead of being silently logged, the emptied-folder prune can never remove the media root itself, and the segment-scratch prune can never remove the segments root.
- **Two recordings with the same title and start minute can no longer overwrite each other** — the immutable recording id is now part of the working filename (the media import renames on filing, so library names are unchanged).
- **A timed-out finalize now kills its ffmpeg** instead of leaving it running against the output file after DVarr reported failure.
- **Capture maps exactly the first video stream** (`0:v:0`) — a multi-programme mux or attached picture can no longer be pulled into the recording.
- **Double-triggered channel ingest can no longer duplicate channels and wedge a source.** Ingest is serialized per source, the channel snapshot is taken inside the write section, and historical duplicate rows are tolerated (lowest id wins) instead of permanently failing every later ingest.
- **Cross-login spreading no longer trusts stream numbers across different providers.** Numeric stream ids are only comparable between credentials of the same provider (same host); across providers a real name/key match is required — previously a recording could be spread to a completely unrelated channel whose number happened to coincide.
- **Reordering a league's channels now validates the list** — a stale drag can no longer half-apply and corrupt the fallback order with duplicate ranks.
- **The scheduler no longer caps candidates before filtering.** The 500-event work cap now applies after the team/session filters, so a block of early non-followed events can't starve later followed ones (a generous 5000-row query cap remains, logged if ever hit).
- **An explicit zero pre/post padding is honoured** instead of being silently replaced by the 5/30-minute defaults.
- **Settings saves are atomic** — the whole Settings page commits in one transaction instead of key-by-key.
- **An over-cap EPG sync now reports the truth**: the run was discarded and the previous guide kept, instead of claiming success with the attempted row count.
- **Login rate limiting is no longer spoofable** — forwarded IP headers are only trusted from the local proxy chain and must parse as real addresses, so a direct attacker can't mint a fresh bucket per request; the bucket store is also bounded now instead of growing forever.
- **The recordings list can't hide imminent recordings** — live/upcoming rows are always returned in full, with the 200-row window applying only to finished history.
- **The health endpoint degrades instead of crashing** when the database fails mid-request, and reports unhealthy when its own queries fail rather than only when a connection can't open.
- **Startup takes a safety copy of the database before applying pending migrations** (kept in `/config/backups`, last three retained).
- **Windows source runs honour configured storage paths** (`DVarr__ConfigDir` etc.) instead of always using the local scratch folder.
- **The preview proxy no longer returns an empty success response** when the provider accepts the request but sends no data, and a mid-stream provider failure aborts the connection instead of ending it cleanly.
- **Activity notifications and scheduler-tick diagnostics are pruned** (30 and 7 days respectively) instead of accumulating forever.

## [1.37.0] — 2026-07-13
Deleting a recording now deletes the file, and a broken dead-feed setting can no longer wreck recordings.

### Fixed
- **Deleting a recording removes its file from disk** (Discord report). Delete previously removed only DVarr's entry — the .mkv, its .nfo/thumbnail sidecars and any leftover capture scratch stayed behind. The delete dialog (single and bulk) now has an "also delete the recorded file from disk" option, ticked by default; the per-game folder is pruned when the delete empties it, while shared show-level artwork is never touched.
- **A bad dead-feed decode setting can no longer kill recordings** (support case: `content_verify_hwaccel=cuda` on a machine with no NVIDIA GPU). The dead-feed check rides the same ffmpeg process as the capture, so a broken decode flag made every launch die instantly — recording nothing while the failover ladder churned through every mapped channel. The recorder now detects two consecutive instant crashes with verification on, drops the verify chain for the rest of that recording (capture itself is unaffected `-c copy`), and posts a warning pointing at the setting.
- **Needs Attention finally says why.** The failure reason was stored and returned by the API but never rendered — the Recordings table now shows it under the state for NeedsAttention / Conflict / Missed / Cancelled / Degraded rows.
- **A failed recording no longer displays the wrong channel.** Failover writes each tried fallback's channel onto the recording, so a purely local failure that captured nothing ended up showing the last channel it tried — which looks exactly like a wrong channel pick (the same support case burned an evening on this). When nothing was captured, the originally scheduled channel is restored; the notification trail keeps the full failover history.

## [1.36.0] — 2026-07-13
Service expiry, a redesigned Leagues page, and bulk actions for mapping and recordings.

### Added
- **IPTV service expiry in Sources.** The Sources table now shows each provider login's **expiry date** (pulled from the Xtream account info the app already fetches), in your configured display timezone, colour-coded when it's close, with a trial flag. A new **Refresh** action re-checks the account (expiry, status, connections) without re-ingesting all channels.
- **Leagues page redesigned.** Each league is now its own section showing its mapped channels in a clear **League → Team → Channels** hierarchy (the team layer only appears when a league has team-scoped channels or followed teams). **Drag the grip handle** to reorder a channel's priority.
- **Map multiple channels at once.** The Map dialog is now multi-select — tick several channels, see them as chips, and **Add all** in one go, instead of one dialog per channel. Ranks are assigned automatically and can be reordered by dragging. (Addresses the channel-mapping feature request.)
- **Bulk actions on Recordings.** Select multiple recordings with checkboxes and **Start / Re-resolve / Stop / Delete** them together from a toolbar that shows how many are selected.

## [1.35.1] — 2026-07-12
Bug-audit batch: five fixes from an external code audit, all verified against a live instance.

### Fixed
- **Manually-armed events now always record.** The scheduler's team-follow and session-follow filters dropped an event the user had manually monitored (the calendar showed it as monitored, but no recording was ever created), and narrowing a follow filter could cancel a manually-armed event's existing recording. Both paths now honour the manual-arm latch, matching the calendar's follow logic.
- **Re-adding a followed team/session re-records its events.** Narrowing a follow filter sweep-cancels not-yet-started recordings; that Cancelled row then blocked the event from ever being re-scheduled, so removing a team and later re-adding it silently lost its games. Filter-sweep-cancelled recordings are now revived (deleted + re-planned fresh) once their event passes the current filters again. A user's own cancellation stays terminal.
- **Segment filename collisions on instant relaunch.** A relaunched capture within the same wall-clock second (clean-EOF instant relaunch, or the 0s first retry) reused the previous process's segment filename and truncated it, losing up to 8s of already-captured footage on a flappy line. Segment names now carry a per-launch counter.
- **Xtream API calls honour the source's custom User-Agent.** Auth, channel ingest, categories and short-EPG always sent the default VLC UA even when a custom one was configured — providers that gate every call on UA would fail discovery while streams worked.
- **Patched a High-severity vulnerability in the bundled SQLite native library** (GHSA-2m69-gcr7-jv3q, `SQLitePCLRaw.lib.e_sqlite3` 2.1.6). Every 2.x release is affected, so the native library is overridden to the patched 3.53.x line (SQLite 3.53); the managed data stack is unchanged apart from EF Core 8.0.10 → 8.0.28. Migrations, WAL mode and reads/writes verified on the new native build.

## [1.35.0] — 2026-07-12
Preview fix, unwatchable-recording fix, and channel picks that show up days ahead. (GitHub issues #7, #8 and #9.)

### Fixed
- **Live preview no longer fails on providers that require a player User-Agent (issue #8).** The in-browser preview proxy was the only provider-facing call that sent NO User-Agent when the source's optional UA field was blank — channel ingest and recording always fall back to a VLC UA, so the list worked and recordings worked while every preview got rejected. The preview proxy and the HLS transcode fallback now use the same VLC default (one shared constant so the paths can't drift again), and when a provider still refuses, the error shows the provider's actual HTTP status (e.g. `403`) instead of the opaque `HttpStatusCodeInvalid`.
- **Recordings can no longer come out as a bogus ~20-hour file that stalls mid-playback (issue #7).** When a provider stream glitches, ffmpeg's transparent `-reconnect` can splice the new connection — with a restarted PCR/PTS clock — into the MIDDLE of a single 8s segment file. That intra-file clock jump slipped past every existing guard (the `setts` repair only fires when PTS and DTS diverge; de-overlap only handles backward jumps; the concat demuxer only re-bases at file boundaries), so the final MKV's timeline leapt hours forward mid-file: players computed a ~20h duration and stalled at the seam. Finalize now probes each segment's internal PTS span and drops any segment spanning more than 5 minutes internally (a clock-cut 8s segment can never do that legitimately) — the glitch costs ≤ ~8 seconds of footage instead of the whole recording, and if every segment were somehow flagged it falls back to the old behaviour rather than producing nothing.

### Changed
- **Guide-match channel picks now happen up to 48 hours out instead of 1 hour (issue #9).** With per-team or multi-channel mappings, placement (days ahead, before the guide has data) falls back to rank order — and the correction sweep only ran within 1h of start, so the Scheduled list showed the same channel for every game all week even though it would self-correct at air time. The sweep now covers everything starting within 48h: as soon as the provider's guide lists the game, the recording moves to the channel actually showing it and the Scheduled list reflects it. All safety rails unchanged (match threshold, hysteresis, same credential only, manual channel locks never moved); the blank-guide refresh chase stays at 1h since an empty guide days out is normal.

## [1.34.0] — 2026-07-12
Per-team channel mappings + the display timezone setting now actually works. (GitHub issues #4 and #5 — thanks to kamsheel and DrZacharySmith for the reports.)

### Added
- **Per-team channel mappings (issue #5).** A league mapping can now be scoped to ONE team: pick the team in the Map dialog and that channel is used only for games that team plays in, beating every whole-league mapping for them. This is how US regional sports networks work (Yankees on YES Network, Mets on SNY inside the one MLB league) — previously a single league-wide mapping sent every game to one channel. Team-scoped mappings never apply to other teams' games; mappings without a team behave exactly as before. Events already carry both teams' TheSportsDB ids (Phase 18), so existing schedules pick this up on the next re-resolve.
- The mappings table shows the team scope, and the Leagues-page help now explains team mappings and the national-broadcast workflow.

### Fixed
- **The Display timezone setting is now actually used (issue #4).** `timezone_display` existed in Settings but nothing read it — every time in the UI was hardcoded to Australia/Brisbane, so a user in New York saw Brisbane times even after setting the timezone. All times in the app — dashboard, recording windows, calendar day-bucketing, guide, event modals, manual schedule/event forms — now render in the configured zone, applied immediately on save (no restart). The setting validates against the real IANA zone database, DST is handled properly (the old code was a fixed +10 offset), and an unresolvable zone falls back to the previous fixed +10 rather than breaking the page. Stored times were always correct UTC epochs — recordings always fired at the right instant; only the display was wrong.
- **Server-side dates follow the display timezone too.** Recording filename date stamps, Plex air dates / season years / episode ordinals, auto-stop notification times, and date-only event anchoring (midnight in YOUR zone, not Brisbane's) all use `timezone_display` now. The Docker image explicitly ships tzdata.
- **Guide-match channel pick can now move a game off its pinned/team channel when the guide shows it elsewhere.** The arm-window re-pick previously ranked candidates by total score, where a pin always dominates — so a nationally-broadcast game never actually moved. It now ranks by guide match: within ~1h of start, if the current channel's guide does NOT look like the event and another MAPPED channel's guide does (same credential, same thresholds + hysteresis as before), the recording moves there. Placement pins are untouched — a pinned channel whose guide shows the game is never abandoned, and manual channel choices (locked) are still never moved.

### Changed
- `/api/health` `time` now reports `local` + `zone` (the configured display zone) instead of `brisbane`.

## [1.33.0] — 2026-07-11
Works out of the box — a TheSportsDB key now ships with the official image.

### Added
- **No more mandatory API-key sign-up.** The official GHCR image ships with a built-in TheSportsDB v2 key, so leagues browse and fixtures sync immediately on a fresh install. Pasting your own key under **Settings → Data sources → TheSportsDB API key** overrides it at any time; clearing the field switches back to the built-in key. The bundled key is injected at image-build time as a Docker BuildKit secret (from the `THESPORTSDB_API_KEY` repo secret) — it never appears in the repository, the image's env/history metadata, the Settings table, or the UI/API (`GET /api/settings` still returns only the user-entered value). Source/local-Docker builds don't include it; they can supply one via the `DVARR_TSDB_API_KEY` env var or in Settings, as before. Closes #2.

## [1.32.0] — 2026-07-09
Self-healing EPG channel matching — guide data for mis-tagged channels.

### Fixed
- **Channels whose provider gives a wrong or missing EPG id now get their guide back automatically.** Some IPTV providers hand a channel an `epg_channel_id` that doesn't exist in their own EPG (e.g. "AU: FOX SPORTS 503" tagged `FoxSports3.au` while the guide keys programmes under `foxsports503.au`), or no id at all — and the EPG itself may ship no channel definitions to name-match against, so the guide came up empty even though the programmes were present. On each EPG sync DVarr now, for any channel whose current id resolves to **zero** programmes, bridges its name to a live programme id by a collapsed-core match (`"AU: FOX SPORTS 503 HD"` ≈ `foxsports503.au`), records that as the matched id, and clears the dead provider id so the guide resolves. It's conservative — ambiguous or too-generic names are left alone — and only ever touches channels that had no working guide, so channels that already resolve are never changed. Verified on live data: healed the Fox Sports AU channels plus ~490 other mis-tagged AU/NZ/SG sports channels, with no effect on working channels.

## [1.31.2] — 2026-07-07
Donation panel polish.

### Changed
- The in-app Ko-fi panel now hides the supporter feed (`hidefeed=true` — the previous `hidefeeditems` param only trimmed it), so the popup is just the donation form.

## [1.31.1] — 2026-07-07
Support-the-creator link.

### Added
- **"🍺 Buy me a beer for the next game"** at the bottom of the sidebar — opens the creator's Ko-fi donation panel in an in-app popup (with a plain link fallback to ko-fi.com if the embed can't load or JS is disabled).

## [1.31.0] — 2026-07-06
Bug-hunt hardening: brute-force limiter, resync double-book, and four smaller fixes.

### Security
- **Login rate-limiter can no longer be bypassed.** It keyed on the first `X-Forwarded-For` hop, which a client controls — so an internet-facing attacker could rotate that header to get a fresh bucket per attempt and defeat the 8-fails/10-min cap entirely, brute-forcing the password at full speed. It now keys on the reverse proxy's real-client header (`CF-Connecting-IP`), which the proxy sets authoritatively and overwrites on every request, so external requests are rate-limited per real client. Falls back to the previous behaviour for direct-LAN requests.

### Fixed
- **A re-synced event that moves could silently double-book a provider login → missed recording.** When an event's time shifted on re-sync, its pending recording was retimed onto the new window with no check that the new window now collided with another recording on the same one-stream login; at record time only one could run and the other was marked Missed. The retime now detects a same-login clash and re-homes the moved recording to a free login (or parks it as a conflict for the next planning pass) instead of blindly overlapping.
- **Negative values in numeric settings are rejected.** A negative pad/interval (e.g. a fat-fingered `-30`) previously saved and silently mistimed every recording; the settings API now requires non-negative integers (the one legitimately-signed setting, the EPG-sync UTC offset, is exempted).
- **Motorsport session classifier** no longer folds a double-digit practice ("Practice 10", "FP12") into "Practice 1" — numbered sessions match as whole tokens.
- **Leagues page** loads with two grouped count queries instead of two per league (N+1 removed).
- A league name containing an apostrophe no longer shows a stray backslash in its dashboard tooltip (wrong escaper).

## [1.30.2] — 2026-07-06
Public-release prep.

### Changed
- **README** — full setup guide: build & first login, TheSportsDB key, adding an IPTV source, channel + EPG ingest, adding a league (team & motorsport follow modes), channel mapping with ranked fallbacks, and the optional Plex / calendar / Home Assistant extras.
- **docker-compose.yml** is now the generic public compose file (optional NVIDIA block commented out). Deployment-specific files (production compose, Unraid CA template, internal build diary) moved out of the repo to local-only.
- Scrubbed personal deployment details (domains, LAN IPs, provider names) from tracked files ahead of the repo going public.

## [1.30.1] — 2026-07-05
Plex episode metadata actually applies now.

### Fixed
- **Plex refresh stopped at the show level.** PMS 1.43 refreshes a show with `?includeChildren=1` and reads seasons/episodes ONLY from an inline `Children` container in that response — it never falls back to the separate `/children` route. DVarr returned the bare show, so Plex updated the show poster and stopped: every episode kept its scanner-generated "Episode N" title and Plex's frame-grab thumbnail instead of the real game title + TheSportsDB artwork. The single-item endpoint now embeds the full child list when asked.
- Unmodeled provider sub-resources Plex probes (`/extras`, `/similar`, …) now return an empty MediaContainer instead of falling through to the SPA's HTML.

## [1.30.0] — 2026-07-05
Searchable Import pickers · Plex-behind-proxy fix.

### Added
- **Import modal: text search.** The League step gains a keyword filter that narrows the dropdown (same UX as the Schedule modal's group filter), and the Game step is now a search box + scrollable list (same as the channel picker) — no more scrolling a 200-option select to find one match.

### Fixed
- **Plex metadata provider behind the reverse proxy.** Every URL the provider handed Plex used the scheme DVarr itself saw — plain `http` behind the TLS-terminating proxy. When the provider is registered via the public https address, Plex's match/metadata POSTs then hit the proxy's http→https redirect and silently die (a 301 turns POST into GET), so matches returned nothing. The provider now honors `X-Forwarded-Proto`, same as the login cookie already did.

## [1.29.0] — 2026-07-05
Plex made discoverable · calendar feed made copyable · calendar layout fix.

### Added
- **Settings → Plex tab.** A dedicated tab explains the Plex Custom Metadata Provider end-to-end: the provider URL with a Copy button (plus the LAN-address note for public-domain visitors), how the matching works (DVarr files recordings Sonarr/Plex-style and answers Plex's metadata requests with real event data from TheSportsDB), the three set-up steps, and the provider identifier for reference.
- **Calendar → Subscribe button.** The token ICS feed is now self-serve: a modal shows the feed address for this device with a Copy button, and a **public address** built from the new `public_base_url` setting (settable right in the modal) for subscribing from Google/Apple Calendar outside the LAN.

### Fixed
- **Calendar month grid overflow**: the 7 day columns used `1fr` tracks, which can't shrink below the widest event pill — long titles pushed the Sunday column past the card edge. Columns are now `minmax(0,1fr)` and event titles ellipsize inside their day cell, so the grid always aligns with the toolbar.

## [1.28.0] — 2026-07-04
Session-smart recording lengths · repo cleanup + new README.

### Changed
- **Built-in motorsport session lengths.** A support session — Practice 1/2/3, Sprint, Sprint Qualifying/Shootout, or Qualifying — now books **1 hour** by default instead of the sport-wide 3 hours; the **Race** (and Testing days) keep the full **3-hour** window. This sits as a new tier in the duration resolution order: your per-session map → per-league override → **built-in session default** → per-sport default → global default. An explicit user setting always beats the built-in. Two safety nets make the tighter windows safe: post-padding still applies, and smart auto-stop keeps extending any live recording the guide says is still running. Practically: an F1 weekend no longer blocks a provider slot for 3 hours per practice session.
- The per-session duration fields in the League modal now show the real defaults as placeholders (60 for support sessions, 180 for the race).

### Docs
- **README rebuilt** with screenshot demos (desktop + mobile PWA), the feature tour, the duration-resolution table, a generic docker-compose quick start, and the architecture map.
- **Legacy name purged.** Every reference to the predecessor DVR's name is gone from tracked files (comments, docs, deploy template, one internal class rename — no runtime behaviour or API surface touched).

## [1.27.0] — 2026-07-04
Trusted devices — sign in once per device, not every launch.

### Changed
- **The browser's Basic-auth popup is gone.** Unauthenticated visits now land on a proper DVarr login page; signing in with "Remember this device" (default on) sets a **180-day session cookie**, so each phone/laptop logs in once. This specifically fixes the iOS home-screen app prompting on every launch (standalone PWAs don't persist Basic credentials; they persist cookies). Sessions are signed with a server-side key in the Secrets store — rotating it signs every device out at once. `POST /api/auth/logout` ends a device's session.
- **Scripts/automation unchanged**: `user:pass` Basic auth is still accepted on every gated endpoint; the machine-to-machine exempt list (health, calendar token feed, Plex, Prowlarr, LAN playlists/streams, Home Assistant) is untouched.
- The login endpoint is rate-limited (8 failures / 10 min per IP → 429) since it's internet-facing.

## [1.26.1] — 2026-07-04
iPhone fixes: schedule-modal overlap + guide defaults.

### Fixed
- **Schedule modal on iPhone**: the Start (local) and Duration (minutes) fields overlapped — iOS renders date-time inputs at a fixed intrinsic width. Side-by-side modal field rows now stack full-width on phones (verified at 390 px).
- **Guide**: default span is **24 h** on every device (was 12 h); on phones the channel column narrows to 78 px so the timeline gets nearly the full width, and **tapping a channel name shows the full channel name** instead of starting a playback.

## [1.26.0] — 2026-07-04
Login protection · every sport type audited · guide QoL.

### Added
- **Login (HTTP Basic).** The UI and API now require a username/password, configurable via docker-compose environment variables `DVARR_AUTH_USER` / `DVARR_AUTH_PASS` (defaults `user`/`password`; put real values in an untracked `.env` — never committed). Machine surfaces keep working without it: container health check, the token-guarded calendar feed (Google), the Plex agent, Prowlarr's keyed API, LAN playlists/streams, and Home Assistant. Constant-time credential comparison; a warning is logged when the defaults are in use.
- The public URL moved to a `dvarr.` subdomain (matches the app name; old `dvr` record removed). *(Server-side DNS/nginx, not in this repo.)*

### Changed
- **Sport-type audit (all 37 TheSportsDB sports).** Follow-up to the UFC fix: verified every TeamvsTeam sport gets the team picker and ONLY Motorsport gets the session picker. Duration defaults now match reality for long sports — golf 6 h, fight cards 5 h, tennis & cricket 4 h (motorsport stays 3 h); auto-stop still covers genuine overruns.
- **Guide:** default span is now 24 h everywhere; on phones the channel column shrinks to 78 px so the timeline gets nearly the full width, and tapping a channel name shows its full name (the play behaviour is desktop's click).

## [1.25.0] — 2026-07-04
Sport-aware league modal · smarter guide-match timing · subscribable calendar · public access.

### Added
- **Subscribable calendar feed** — `GET /api/calendar.ics?token=…` (token auto-generated, kept in the Secrets store; `GET /api/calendar/url` returns your URL). One entry per upcoming monitored event honoring your team/session follow filters, with a 30-minute reminder alarm. Built for Google Calendar's URL subscription via the new public domain.
- **Public access** — DVarr is reachable from outside the LAN via a reverse proxy (SWAG + Cloudflare). The credential-leaking stream-proxy redirect and the slot-burning live preview are blocked from outside (403); LAN unaffected. *(Server-side config, not in this repo.)*

### Changed
- **League modal is sport-aware** — the session picker + per-session lengths now appear ONLY for motorsport (UFC/boxing/etc. no longer get a bogus "Race" session picker); "Recording stop" reads *"Auto — extend while the event is still live (via TheSportsDB)"*; max-extension shows a plain `60` placeholder and pre-fills `120` when you pick a motorsport league (never clobbering a saved or typed value).
- **Guide-match channel pick now runs ~1 hour before start** (was 24 h — the provider's guide often has nothing that far out), and if the source's EPG is more than 12 h old it refreshes the guide first, then re-checks. Migration Phase22 stamps each source's last successful EPG sync.
- **Mobile**: the four status cards are gone from the phone dashboard; the topbar drops the status dots and fits the full page name, with Schedule + the ⋯ menu at the far right.
- The accounts/multi-user plan is scrapped (docs/12 updated) — the calendar + public URL ship without it.

## [1.24.0] — 2026-07-04
Mobile-first overhaul — the phone UI is now genuinely app-like.

### Changed
- **Phones (≤640 px) get a purpose-built layout with zero sideways scrolling on every page.** All wide tables (Leagues, Channel mappings, Recordings, Channels, Sources, Conflicts, Activity) reflow into stacked cards with labeled fields, and every row's action buttons tuck behind a **⋯ menu** (same actions, tidier). Secondary topbar actions collapse into a topbar ⋯; the primary button stays visible.
- **Drawer navigation** polished: scrim, slide animation, Escape/scrim/nav-tap close, body scroll-lock, 44 px touch targets, iOS safe-area padding — works installed as a PWA.
- **Modals become full-screen sheets** on phones (own scroll region, sticky title + action footer, ≥16 px inputs so iOS doesn't zoom, 2-column touch team/session pickers).
- **Calendar on phones** is a tappable mini-grid (colour dots per day) with the selected day's events listed below; the guide pans inside its own timeline only. Dashboard panels stack in priority order with the KPI row 2×2.
- **Desktop is pixel-identical to v1.23.0** — every change is scoped behind mobile media queries.

## [1.23.0] — 2026-07-04
Smart auto-stop (no more missed extra time) · full UI redesign · league filters.

### Added
- **Smart auto-stop.** Recordings no longer cut off when a game runs long (extra time, penalties, red flags). Near the scheduled end, DVarr checks TheSportsDB: while the guide says the event is still in play (or gives no signal — motorsport), the recording **extends in 15-minute steps**, capped per league (default +60 min, motorsport +120 min) and never into the next recording's slot on the same login; once a terminal status (FT / AET / **AP — After Penalties**, the exact status of the Australia v Egypt game this missed) is reported, it closes after the normal post-pad. Auto never shortens a window. Per-league control in the league modal ("Recording stop": Auto [default] / Fixed + max extension); kill-switch `auto_stop_enabled`; every extension/close shows as an `AutoExtended` entry in the Activity feed. Migration Phase21.
- **League filters**: the Recordings page can filter by league (recordings now carry their league), and the Leagues page's channel-mappings table gets a league filter + aligned full-width layout.

### Changed
- **Complete UI redesign** — new deep-navy design language across every page: KPI stat cards, icon-chip panel cards with count pills and "View all" links, pill status badges, gradient primary buttons, redesigned sidebar (active-state accent bar, version chip) and topbar (slots + Database status chips), rebuilt dashboard (Recording Now / Scheduled 24h / Recently Completed / Sources / Next 24 Hours / Leagues panels), restyled tables, modals, tabs, guide and calendar.

### Docs
- `docs/12-remote-access-and-calendar.md` — full design (nothing built yet) for the subscribable calendar feed, external access via SWAG/Cloudflare, and built-in accounts/roles with a simplified member UI. Includes a security finding: the IPTV stream-proxy redirect embeds provider credentials — external exposure stays blocked until auth ships.

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
- **Sonarr/Plex-style finished-game filing**: per-game folder `<Title> (yyyy-MM-dd) E<NN>/` plus an `HDTV-<height>p` resolution tag, e.g. `FIFA World Cup/Season 2026/USA vs Paraguay (2026-06-12) E04/FIFA World Cup - S2026E04 - USA vs Paraguay - HDTV-2160p.mkv`.
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
- **Deployed to Unraid as a Docker container**, replacing the previous DVR — `dvarr:latest` on port `1867`, volumes mapped (`/config`→appdata, `/media`→Media/Sports, `/segments`→array scratch), Unraid dashboard logo + WebUI link.
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
- **Event data + anti-hijack resolver** (league/event auto-record layer): leagues, churn-proof event upsert by natural key, pinned league→channel mapping with same-credential fallbacks, and a background auto-scheduler that creates Pending recordings within each league's horizon.
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
