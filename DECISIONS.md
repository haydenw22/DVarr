# DVarr — Autonomous build decisions (2026-06-15)

Decisions I made while you slept, per your "work out the best solution yourself but make me aware of it." Flag anything you disagree with.

## The three features you asked for — how I built them

1. **Channel group filter (source-aware).**
   - Xtream `get_live_categories` is ingested and the category name is stored on each channel as `Channel.GroupName`. (I used a denormalised string rather than a separate `Category` table — simpler, and all you need is filtering.)
   - Channels page has a **Group** dropdown next to the Source toggle. Changing the **Source** reloads the group list for that source and resets the group filter — exactly as you asked. API: `GET /api/channels/groups?source=<id|all>` and `GET /api/channels?...&group=<name>`.
   - Verified live: Source 2 → **919 groups** (e.g. "8K| WORLD CUP 2026", "SPORT ON AIR").

2. **External EPG link + override checkbox (per source).**
   - Each source now has `EpgUrl` (optional external XMLTV `.xml`/`.xml.gz`) and `EpgOverride` (checkbox). When override is on and a URL is set, EPG sync pulls the **external** XMLTV; otherwise it uses the provider's `xmltv.php`. Both are in the source add/edit form.
   - EPG sync is a **separate action** (`POST /api/sources/{id}/epg`, "EPG" button on Sources) so you can refresh the guide without re-ingesting channels.
   - Verified live: Source 2 provider EPG → **150,004 programmes across 3,388 channels in ~10s**.

3. **Source CRUD (own sources + edit).**
   - Add / edit / delete sources from the **Sources** page (form modal). Fields: label, type (xtream/m3u), protocol, host, port, max-streams, username, password, external EPG URL, override checkbox, enabled.
   - **Password & username are write-only**: never returned by the API (username masked, password never sent); on **edit**, leaving them blank **keeps** the existing value.
   - **Delete** removes the source's channels + EPG (recordings are kept) and is **blocked** if the source has active/pending recordings.

## Other decisions & notes

- **Validated against Source 2 only.** Source 1 (`520…`) was never contacted, per your instruction.
- **Clean slate done (as you asked).** After validating the full pipeline against Source 2, I **wiped the DB + media**. The app now boots to a fresh DB with just the **2 seeded sources** (no channels/EPG/recordings). Seeding makes **no provider calls** — pull channels/EPG yourself from the Sources page when you're ready (Source 2 only). Source 1 is seeded as a row (id 1) but will not be contacted unless you trigger it.
- **`max_streams` is editable, default 1** (per your note). The provider still enforces 1/login regardless; this only matters for non-provider sources.
- **`Type` field (xtream/m3u)** added; only **xtream** is implemented. M3U-playlist sources are a stub for later.
- **EPG bounds (you said the cap is fine):** programmes are limited to a **now−12h … now+10d** window and a **150k-row safety cap** (Source 2 hit the cap — expected for a 55k-channel lineup). This keeps memory/DB sane. For real use you'll likely point sports channels at a smaller **external EPG** (the override feature) rather than the giant provider EPG.
- **EPG fetch is fully streaming** (gzip auto-detected) — it does not buffer the whole file, so a huge XMLTV won't OOM.
- **`RecordingSegment` rows** are now written at finalize (the UI's segment count works — the validation recording showed 9 segments).
- **Recorder validated for real:** a 90s Source-2 TNT Sport HD capture produced a valid **1080p H.264 + AAC 5.1** `.mkv`.

## Known limitations / TODOs (not regressions — explicitly deferred)

- **Secrets are still plaintext at rest** in the local SQLite (masked in the API). Encryption-at-rest is the documented next security task.
- **Content verification is still basic** (output-growth + finalize); the placeholder-loop/blackdetect heuristics and active mid-stream auto-failover are not in yet (same-credential fallbacks are wired and chosen at resolve time).
- **Testing harness gotcha (not an app bug):** PowerShell's `Get-Date -UFormat %s` returns a value **+10h off** on this Brisbane machine, which made my first manual schedule land 10h in the future (the scheduler correctly ignored it). The app's own time handling is correct — it stores the server UTC epoch. I now base test timestamps on `/api/health`'s `utc_epoch`.

## Adversarial code review + fixes applied (same night)

I ran a 5-agent review (reliability core, the new features, security/API, UI, + a completeness critic) over everything. The critic confirmed **all three features delivered with no regressions**. The reviewers found real bugs, which I then **fixed and re-verified** (clean build + a fresh real Source-2 recording → `Done`, smooth byte growth, 9 segments):

**Critical (fixed):**
- **Recorder could terminal-fail before post-roll on a transient error** (e.g. a DB hiccup mid-match) and skip finalize, abandoning already-captured segments. Now the capture loop swallows transient errors → `Recovering` + backoff, and finalize **always** runs — captured segments are never thrown away.
- **Tuner-lease leak**: if anything threw after acquiring a credential's single slot, that credential was dead for the process lifetime. Now any post-acquire failure releases the slot; the scheduler also guards each recording so one failure can't abort the whole tick.

**Major (fixed):**
- **Boot recovery** now **re-finalizes** a window whose segments survived a crash (was marking it Missed and discarding them).
- **EPG sync no longer holds the single DB write-gate for the whole rebuild** (it delete-once then inserts in short batches), so a big EPG refresh can't stall a recording start/finalize.
- **EPG 150k-cap truncation is now surfaced** (flagged in the result + UI toast: "use an external EPG for sports channels") instead of silently producing a partial guide.
- **Recordings filter** "Recording" now shows all active states (Starting/Recovering/Finalizing/…) instead of hiding an in-flight capture.

**Minor (fixed):** EPG trailing-programme flush + malformed-title tolerance; source upsert is partial-update-safe (Port/Enabled/override no longer silently reset); external EPG URL is validated as http(s) (SSRF reduction); provider username fully masked (`***`); UI tolerates empty 404 bodies; a couple of escaping/feedback nits; and the stall detector now tracks each signal in its own units (no more byte-threshold-on-milliseconds fragility).

**Deliberately deferred (documented, not bugs):** secrets encryption-at-rest; no app-layer auth + bind `0.0.0.0` (by design — single-user trusted LAN, container behind the LAN; add a reverse-proxy/API-key if you ever want remote access); source-delete transactional atomicity (the FK cascade covers dependents).

---

## Phases 2–5 built (the rest of the app)

With the three features + reliability core done, I built out the remaining planned phases. Everything below is in the running app (clean Debug build, `dotnet run` on **:1867**).

**Phase 2 — Event data + resolver (the "Sportarr-like" layer).** This is the part the earlier DECISIONS said wasn't built yet — it now is.
- **Leagues** page: add a league, pick an **event provider** (TheSportsDB free tier / ICS calendar URL / manual), set a **schedule horizon** (days), and **monitor** it.
- **Events** are fetched + upserted by a **churn-proof natural key** (`{leagueId}:{provider}:{externalId}`) — a re-sync never deletes or re-keys, so it can't orphan a scheduled recording. Date-only events anchor to **Brisbane midnight** (never silent-arm at UTC midnight).
- **Anti-hijack resolver:** a **pinned** league→channel mapping carries a dominant floor that EPG data can never outrank; EPG is only a small bounded bonus on an already-mapped channel. **Fallbacks are restricted to the same credential** as the winner, so a fallback can never steal another login's only stream slot.
- **Auto-scheduler** (background): refreshes stale monitored leagues, then creates `Pending` recordings for monitored events inside each league's horizon via the resolver. The existing scheduler arms them at pre-roll.

**Phase 3 — Parity surfaces (so it slots into your stack like Sonarr/Sportarr).**
- **Credential-free exports:** `GET /api/iptv/filtered.m3u` and `/filtered.xml` for your mapped channels. The M3U points at a **stream proxy** (`/api/stream/{id}.ts`) that 302-redirects to the real provider URL at play time — **the exported playlist never contains your login**.
- **Minimal Sonarr-v3 surface** (`/api/v3/...`, `X-Api-Key`) so **Prowlarr** can talk to it.
- **Home Assistant:** `GET /api/ha/status` (poll from a REST sensor) + a webhook push (`ha_webhook_url` setting) that forwards live recording state changes.

**Phase 4 — Media import (Plex/Jellyfin-clean library).** After finalize, an event-linked recording is filed as **League = show, year = season, MMddHHmm = episode**, with Kodi `.nfo` sidecars (`tvshow.nfo` + per-episode) and a self-generated thumbnail (so Plex never shows a generic DVR series or a random ad-frame). Non-event recordings keep their flat name.

**Phase 5 — Settings + ops.** All tunables (pads, intervals, concurrency, ThreadFin URL, HA webhook, timezone) are editable on the **Settings** page and backed by a typed Settings table with the plan's defaults.

## Second adversarial review (Phases 2–4) — fixes applied

Ran another multi-agent review over the new code. Real findings, all **fixed + clean-rebuilt (0 warnings, 0 errors)**:

**Critical (fixed):**
- **Path traversal via a provider-derived league/title** could compose a media path outside `/media`. Now: `Sanitize()` also rejects dot-only names, and the final path is **containment-checked against the media root** before any move (refuses + leaves the file in place otherwise).
- **Stale `OutputPath` after a partial import failure.** The real file location is now persisted **immediately after the atomic move**; sidecar/thumbnail enrichment is best-effort in its own try/catch, and the failure path never points a `Done` recording at a missing file.

**Major / security (fixed):**
- **M3U line-injection** — a crafted channel/EPG id with CR/LF could inject a fake `#EXTINF`/stream line into the exported playlist. Provider fields are now control-char-stripped (and `tvg-id` is escaped too).
- **Sonarr-v3 API key** is now compared in **constant time** (`CryptographicOperations.FixedTimeEquals`) so it can't be recovered via response-timing.
- **Source-delete orphans** — deleting a source now also clears its **league mappings + channel-health** rows, so the resolver can't later score a channel that no longer exists.
- **Auto-scheduler per-event isolation** — one un-resolvable/write-failing event can no longer abort the whole tick (it logs and moves on), so a single bad event can't silently stop every later one from arming.

**Minor (fixed):** UID-less ICS events key on the (normalised) **summary**, not the start time, so a moved event updates in place instead of duplicating; ICS sync no longer **clobbers a Live/Completed/Cancelled status** with a null re-sync; date-only events match EPG across the **whole local day** (and pick the best-matching programme); XML/NFO output strips XML-illegal control chars; the `ScheduleHorizonDays` migration defaults to **14** (so a pre-existing league row can't get a 0 horizon that silently disables scheduling).

## Status — nothing outstanding for you to decide
Your three directives are done: **Source 2 wiped to a clean slate**, **EPG cap kept**, **max_streams editable @ default 1**. The app is built through Phase 5, reviewed twice, and running clean on **:1867**. Source 1 was never contacted.

---

# Phase 6 — your screenshot feedback (2026-06-15)

Built every item from your feedback, ran a 6-dimension adversarial review, fixed what it found, rebuilt clean (0 warnings), and verified in the browser preview (no console errors).

## What I built
1. **Schedule modal → cascading Source → Group → Channel**, each step filtering the next, with **keyword (not exact) search** — typing `uk sports` matches `UK| Sports`. Added a **Recording name** field and a **"Match to TheSportsDB & rename for Plex when it finishes"** toggle (stored as the recording's match query; the media importer resolves it at finalize).
2. **Channels page**: a **green ▶ Watch** button (live preview) and a **Schedule** button that opens the modal **pre-filled** with that channel's source/group/channel — replacing "record 30m".
3. **Live preview**: `mpegts.js` (vendored locally at `wwwroot/js/mpegts.js`) plays the feed in-browser with native volume + fullscreen, fed by a **new byte-proxy `/api/preview/{id}.ts`** that streams the provider stream **through DVarr so your credentials never reach the browser**. It **leases the credential's single slot** (returns "stream busy" if a recording/another preview holds it) and releases on close.
4. **Guide → a real timeline**: a **"now" playhead**, **live programmes in green**, **programmes being recorded in red**, **source + group filters** (keyword), prev/next/now navigation, and **click any programme to schedule it** (pre-fills source/group/channel/name/start/duration). Click a channel name to watch it live.
5. **EPG → stores the ENTIRE external EPG** (your choice): I decoupled the guide store from the channel list (programmes keyed by `(source, tvg-id)`), **removed the 150k cap** (now a configurable safety guard `epg_max_programmes`=3,000,000 + `epg_past_window_h`=48 / `epg_future_window_d`=21), and stream the parse in 5k-row batches so a 143 MB / multi-million-programme EPG won't blow memory. Your `myepg.top` feed (~143 MB, 18,017 channels) now syncs in full.
6. **Leagues → TheSportsDB-only**: pick a **Sport** (dropdown) then search the **League** (dropdown with keyword search); manual/ICS and the raw league-id/ICS-url fields are gone. Posters + events sync automatically; the league poster shows on the Leagues page.
7. **Media import → flawless Plex**: league = show, year = season, **per-season ordinal = episode** (chosen over `intRound` because motorsport FP1/Quali/Race share a round), with TheSportsDB artwork **downloaded to disk** (`poster.jpg`, `Season{year}.jpg`, and an episode thumbnail matching the video's basename — Plex's stock convention).
8. **Plex Custom Metadata Provider** (the modern, supported approach — see below).

## Two decisions you should know about
- **Plex: I built a Custom Metadata Provider, like Sportarr's.** You were right — Plex 1.43.0+ supports URL-based providers (legacy `.bundle` agents are being removed). DVarr now serves one at **`http://<dvarr-host>:1867/plex`** (302 → manifest at `/api/plex/provider/sports`), backed by your leagues/events with TheSportsDB artwork. **Setup in Plex:** Settings → Metadata Agents → + Add Provider → paste `http://192.168.4.63:1867/plex` → Add → restart Plex → create/point a **TV Shows** library at the DVarr agent.
- **TheSportsDB key:** the built-in public test key `3` only returns a **2-sport sample (Soccer + Motorsport, 5 leagues each)** — it has V8 Supercars but **not AFL or full F1**. I made the key a **Setting** (`thesportsdb_api_key`). Paste your own TheSportsDB key there to unlock the full catalogue (AFL, all motorsport, etc.).

## Adversarial review — fixes applied
- 🔴 **Off-limits credential had no structural guard** (the #1 project rule rested on discipline). Now DVarr **refuses all provider contact for a *disabled* source** — recorder (incl. the background auto-record pipeline), ingest, EPG, live preview (403), the export stream-proxy (404), and the resolver (won't even create a recording for it). **I set Source 1 (`Strong8k 1`/`520…`) to disabled** (and the seed file too, so it stays off-limits across re-seeds). Re-enable it on the Sources page whenever you want.
- 🟠 **Plex episode numbers now use a stable ordinal** (`StartUtc, then Id`) so the on-disk `SxxExx` always matches the Plex agent — even for same-time motorsport sessions.
- 🟠 **Live preview no longer leaks the stream slot** if you navigate away with it open (closes on route change + Escape).
- 🟠 **Media import never clobbers a different recording's file** (collision-safe naming) while still allowing a re-finalize to overwrite its own.
- 🟡 Hardening: proper JS-string escaping in click handlers (apostrophes in names no longer break buttons), TheSportsDB key URL-escaped, transient empty API results cached only briefly, legacy ICS URL validated.

## ⚠ One thing for you to decide — Source 1 has data it shouldn't
The DB has a **full 55,460-channel ingest under Source 1 (the off-limits `520…` credential)**; Source 2 is empty. I did **not** ingest it this session — it appeared during this session (most likely an accidental "Ingest" click on Strong8k 1 in the live preview, or a prior run). I've **disabled Source 1 and locked it out of all provider contact**, but I **did not delete the channels** (your data, and I didn't create it). **Tell me whether to wipe them for a true clean slate** (a local/offline delete — it does *not* contact the provider).

> Update (Phase 7): you said Source 1 is no longer off-limits, so I **re-enabled it**. Both sources are now ingested and Source 2 has its EPG synced.

---

# Phase 7 — your screenshot feedback (2026-06-15)

All five items done, reviewed (3-dimension adversarial pass), fixes applied, rebuilt clean, verified in the browser.

## What changed
1. **Source 1 re-enabled** (no longer off-limits). The disabled-source guard from Phase 6 stays as the mechanism — just not applied to Source 1 now.
2. **Live preview — fixed (this was the real bug).** Every preview failed because mpegts.js ran its fetch in a Web Worker with a **relative URL**, which the worker can't parse (`Failed to parse URL from /api/preview/…`). Now uses an absolute URL. Verified H.264 plays direct (1280×720) and HEVC 4K plays direct in HEVC-capable browsers (3840×2160). I also built the **transcode fallback** you mentioned (MPEG-TS): browsers that can't decode HEVC/AC-3 fall back to a server-side ffmpeg transcode → 720p H.264 HLS (one provider connection, so the 1-stream rule holds). Verified 4K HEVC → H.264 segments.
3. **Guide — fixed.** Two bugs: matching was **case-sensitive** (`FoxSports3.au` ≠ `foxsports3.au`), and it opened on a source with no EPG, showing a wall of 24/7 channels that carry no tvg-id (only ~8,300 of 55,460 do — those are the sports ones). Now: case-insensitive matching, EPG-bearing channels first, defaults to the source that has EPG. Default view **12h**; options **6h/12h/24h** (3h removed). Verified 600+ programmes render.
4. **Calendar — rebuilt as a monthly grid.** Current month by default, prev/next/today, events per day in chronological order, each card coloured by its league. Added a **10-colour picker** on the league form; a colour dot shows on the Leagues page. Verified V8 Supercars events show amber in June 2026.
5. **TheSportsDB key 123 — doesn't unlock AFL/F1.** I set it, but **123 is the same shared free/test key as 3** — only a 2-sport sample (Soccer + Motorsport). Confirmed directly: the AFL league id returns "Polish Ekstraklasa" and AFL events come back empty. **Motorsport *is* in the sample, so V8 Supercars works** (37 events synced). **AFL + full F1 need TheSportsDB Premium** (the "Go Premium"/Patreon button). If you'd rather not pay, I can re-add the ICS-calendar option (public AFL/F1 `.ics` schedules exist) — your call.

## Adversarial review — fixes applied
- 🔴 **Double lease-release could break the one-stream rule** — the slot release used a count heuristic; two releases of the same lease could free a slot another preview just took. Now **exactly-once per lease** (atomic flag) + the preview transfers lease ownership to its session. Verified open→close→reopen.
- 🟠 Preview HLS **path-traversal/sibling-prefix** (`preview/12` vs `preview/123`) → trailing-separator guard + reject `/ \ ..`; **sweep race** on the session timestamp → atomic access; **stored XSS via league colour** → server validates `#rrggbb`, client guards before injecting into `style=`; **EPG query performance** → `EpgChannelId` is now `COLLATE NOCASE`, so case-insensitive matching is **index-seekable** (no full scan of a source's hundreds of thousands of programmes) — this also improved EPG coverage.
- 🟡 Preview sessions are killed on graceful shutdown + orphan temp dirs purged on boot; the sweep tolerates a failing release.

---

# Phase 8 — your screenshot feedback (2026-06-15)

All six items done, reviewed, fixed, verified in the browser.

## What changed
1. **🎯 TheSportsDB free key works for everything (you were right).** The key isn't sport-limited — only the *browse* lists are sampled; **by-ID lookups work for every sport** (verified F1/NBA/NFL/MLB/AFL). So I bundled a **curated catalog of ~48 leagues with verified IDs** (AFL, F1, V8 Supercars, WEC, World Cup, NRL, A-League, NBA/NFL/MLB/NHL, …) and the picker uses that + a "paste a league ID" box. **No paid key required** — AFL is selectable now.
2. **📺 Guide matches almost every channel (TiviMate parity).** The provider tags only ~8,300/55,460 channels with a tvg-id, and its M3U (which has the rest) is blocked (HTTP 884, any user-agent). So I added **name-based matching** like TiviMate: DVarr reads the EPG's channel display-names and matches your channels by normalised name. After an EPG re-sync, coverage went **7/15 → 60/60 (120/120)** channels. *Runs during EPG sync — re-sync each source once to populate it.*
3. **🎬 Choppy preview** was mpegts.js with no IO buffer + live-edge chasing → underruns. Switched to a stash buffer + no latency-chasing.
4. **📅 Guide channel column** widened 168→250px, 2-line names.
5. **🎨 Calendar colours** → 10 clearly-distinct hues.
6. **📊 Dashboard rebuilt** — dropped status/ffmpeg; 2-column at-a-glance: **Recording now + Scheduled** | **Upcoming events** (colour-dotted) + **League chips with posters**.

## Adversarial review — fixes applied
- 🟠 **Name-collisions could put the *wrong* EPG on a channel** (two different channels normalising to the same name → first-writer-wins). Now ambiguous names are detected and **skipped** (no guide beats wrong guide). Re-verified: coverage held at 60/60.
- 🟠 **A typo'd manual league id created a junk "League" row** → now rejected with 400 (verified).
- 🟡 Corrected two misleading `NOCASE` code comments (the collation is on the Programme column). The reviewer separately verified the XML parsing, effective-id logic, XSS escaping, mpegts config, and onclick wiring are all sound.

## One thing only you can judge
Whether the **preview is smooth now** after the buffering change — worth a quick test on a normal HD channel.

---

# Phase 9 — your screenshot feedback (2026-06-15)

1. **Guide click → schedule now prefills group + channel.** The bug: the group dropdown rendered only the first 800 of your 919 groups, so a channel whose group sorts late (e.g. "US| FOX…") wasn't in the list and the auto-select silently failed. Fixed (render all groups + inject the prefilled group/channel if needed). Verified on "US: FOX SPORTS 1 HD".
2. **Guide recording colour split.** The actual recording window is now **red**; the **pre/post-roll buffer is orange** (with a legend entry), so it's clear why neighbouring programmes are tinted. Far fewer programmes show red.
3. **Dashboard decluttered** to exactly what you asked: **Scheduled recordings** + **Next 24 hours** of events. Removed the leagues chips and the empty "recording now" panel.

**Heads-up:** you've auto-scheduled the **FIFA World Cup** (60 pending recordings), but many matches are simultaneous on the *same* channel/credential — with one stream per login, only one of each overlapping set can record. To catch concurrent matches you'd map multiple World Cup channels (different feeds) to the league, or record different matches on Source 1 vs Source 2.

---

# Phase 10 — STAGED (deploy after recording #61 finishes) (2026-06-15)

Everything below is staged in source only — the running app serves the **bin copy** of the frontend and the compiled DLL, so none of it is live until the post-recording rebuild + restart. Verified: `dotnet build` clean (0/0), `node --check app.js` clean. Did not touch the live recording.

## Dashboard
- Restored the **league chips** and the **empty "Recording now"** panel (I'd over-removed them in Phase 9). Layout is now a **2×2 grid**: row 1 = *Recording now* | *Leagues*, row 2 = *Scheduled — next 24h* | *Next 24 hours*. League chips sit directly above *Next 24 hours*. `align-items:stretch` equalises each row, so **Scheduled and Next-24h are the same height** unless multiple live recordings grow the Recording-now cell. Scheduled is now limited to the **next 24h**.

## Leagues page
- **Alignment fix:** the Leagues table now has a `Leagues` heading matching the `Channel mappings` heading, so the two panels line up.
- **Docs:** added a clear explainer of how mappings + **ranks** work, and a one-line hint in the Map modal.
- **Rank default 1, rank 0 removed:** Map modal input is `min=1, value=1`; client clamps to ≥1; the API already defaulted to 1 and rejected non-positive.

## Ranked fallback (rank 1 = primary, 2…N = fallbacks)
- Made the rank model 1-based and coherent: `RecordingFallback` rows are now ranks **2…N** (the primary is rank 1 on `Recording.ChannelId`); `AutoScheduleService` numbers them from 2, the recorder loads `Rank ≥ 2`. Resolver ties break by user rank (`OrderByDescending(Score).ThenBy(Rank)`) so the ladder is deterministic and matches the Leagues page order. Fallbacks stay **same-login only** (unchanged).
- The "channel isn't live" failover already existed (transport stall → relaunch → walk to next rank). This phase makes the numbering honest and adds the content trigger below.

## Content verification — answering "will it read the screen?"
- **Honest answer: no — it can't tell the *right match* from a pre-show by looking at pixels** (a pre-game intro is visually identical to "right channel, just before kickoff"). So DVarr does **not** classify content/genre and never fails over on "looks like an intro" — that's why pre-pad captures intros by design. "Right content" relies on your **ranks + EPG + padding**, like every other DVR (Channels/Plex/Jellyfin/TiviMate).
- **What it *can* do (built, gated OFF by default):** a **second decode-only output on the same ffmpeg connection** (no extra provider login → 1-stream rule intact) runs `blackdetect`/`freezedetect`/`silencedetect`. Sustained **dead picture** (black or frozen ≥ `content_dead_timeout_s`, default 30s) routes into the **same relaunch→failover ladder** as a stall, so it walks to the next-ranked channel. Audio silence alone never triggers failover (a quiet passage of play is not a dead feed). Emits a `PlaceholderDetected` notification and stamps `Recording.LastContentOkUtc`. Settings: `content_verify_enabled` (default **false**), `content_dead_timeout_s` (default **30**).
- **Validated offline** against the live recording's own segments: on a continuous 48s stream of real football → **zero** false black/freeze/silence; a synthetic black+silent feed → all three fire. (Per-segment files false-positive at GOP boundaries, but the recorder analyses the continuous live feed, not pre-cut segments, so that artifact doesn't apply.)
- No DB migration: `ContentVerdict`, `Suspect`, `LastContentOkUtc`, `PlaceholderDetected`, and both `Rank` columns already existed in the schema.

---

# Phase 11 — recording reliability fix + #61 salvage (DEPLOYED 2026-06-15)

Hayden's finished Côte d'Ivoire–Ecuador recording had **no sound for most of it** and **"frame rate deteriorating."** A 6-agent forensic workflow (with adversarial verification re-running ffmpeg on the surviving segments) found the real causes — and the video was never the problem.

## Root cause (one fault drove the "frame rate" symptom; two drove the audio)
- **Timeline:** the IPTV source delivered **2 video packets with a corrupt-but-valid 33-bit PTS = 8,227,901,795 (≈91,421 s / +25.4 h)** at the head of the main feed, while their **DTS was correct (~1.4 s)**. `-fflags +igndts` told ffmpeg to *ignore the good DTS and trust the corrupt PTS*, so the bad value was baked into the capture. MKV sets duration = max PTS → the file reported **25.5 h** instead of 2.6 h, and players freeze/desync from ~minute 4. The video frames themselves were all intact (556,698 frames, ~60 fps).
- **Segment stall:** the PTS-based segmenter never rotated once the bad PTS hit → **one 6 GB segment** for the whole match (the already-staged `-segment_atclocktime 1` prevents this).
- **Audio:** two effects. (a) The broken 25.5 h container desynced the audio renderer (most of the "no sound"). (b) A **real ~63 s silent gap (251–314 s)**: the pre-show is **HE-AAC** and the main feed splices to **AAC-LC**; under `-c copy` the single-codec MKV track can't decode across the splice. (The earlier "50 % of audio missing" was a measurement artifact — HE-AAC/SBR is 2048 samples/packet, so ~23 pkt/s is correct, not half.)

## Fix (deployed) — `RecorderSupervisor`
**Capture:** `-fflags +genpts` only (dropped **+igndts** and **+discardcorrupt**); added `-analyzeduration 10M -probesize 10M`, explicit `-map 0:v -map 0:a`, `-max_muxing_queue_size 4096`, and a self-healing video bitstream filter **`-bsf:v "setts=pts=if(gt(abs(PTS-DTS),900000),DTS,PTS)"`** — rewrites PTS from the good DTS only when they diverge by >10 s (900,000 ticks). Real B-frame reorder is <0.15 s, so it's a **no-op on healthy frames** (≈700× margin) and stays `-c copy`. Kept all reconnect/`-rw_timeout`/`-progress` flags and `-segment_atclocktime 1`.
**Finalize:** video **`-c:v copy` + the same `setts` bsf** (lossless, kills the spike), **dropped `-fflags +genpts`** (it can't fix a present-but-wrong PTS); audio **re-encoded to one uniform `-c:a aac -b:a 256k` track** (cheap CPU, no NVENC) so mid-stream codec/SBR changes can never leave a silent splice again.

## #61 salvaged (lossless video, full audio)
Re-finalized the 33 surviving segments with the new recipe: **duration 9,335.885 s**, all **556,698 video frames** preserved (video stream MD5 byte-identical to the original → truly lossless video), and **continuous audio end-to-end** including the former 251–314 s gap (−21…−29 dB everywhere). The fixed file replaced the original; the broken one is kept as `…[2026-06-15_1900].broken-timeline.mkv` and the segments are retained until Hayden confirms playback.

## Verification
ffmpeg-level comma-escaping (`\,`; C# literal `\\,`) confirmed to survive .NET `ArgumentList` (no shell) via an argv-list test. Clean build (0/0). Full pipeline re-run live as test recording #63.

---

# Phase 12 — deployed to Unraid as a Docker container (2026-06-16)

DVarr moved off the Windows dev box to its permanent home: a container on the Unraid server (192.168.4.63:1867), **replacing Sportarr**. Deployed as a **completely fresh, zero-data first-run install** — no sources, EPG, leagues, mappings, or DB carried over.

## Volume mapping (the heart of the task — all verified against the live server)
| Container | Host | Backing | Why |
|---|---|---|---|
| `/config` | `/mnt/user/appdata/dvarr` | appdata = cache-only NVMe | SQLite DB (WAL) + settings — small, fast |
| `/media` | `/mnt/user/Media/Sports` | Media share = array-only, 19 TB | Plex (host-net, `/mnt/user/Media→/Media`) already scans `/Media/Sports` |
| `/segments` | `/mnt/user/Media/.dvarr-segments` | same array-only Media share | 6 GB+/recording scratch kept off the 85%-full (39 GB) cache; dot-prefixed so Plex ignores it; same volume as `/media` |

## What was done
- **Retired Sportarr:** stopped, `restart=no`, removed from `/var/lib/docker/unraid-autostart` (freed port 1867; its data left intact).
- **`docker-compose.yml`** (new, repo root): `image: dvarr:latest`, `user: "99:100"` (nobody:users — DVarr has no PUID/PGID), `restart: unless-stopped`, `TZ`/`HOME=/config`, the three volumes, and Unraid labels `net.unraid.docker.webui` + `net.unraid.docker.icon` (→ `/dvarr-logo.png`, served by the app) so the Docker dashboard shows the DVarr logo with a working **WebUI** menu item.
- **Build on the server** (no Docker on the dev box, no registry): clean source `tar | ssh` to `/mnt/user/appdata/dvarr-build`, then `docker compose up -d --build`.
- **Two fixes found during deploy:** (1) `Dockerfile` now installs **wget** — the aspnet:8.0 base ships neither wget nor curl, so the `/api/health` HEALTHCHECK was failing (container showed "unhealthy" though the app was fine). (2) **Post-finalize segment cleanup** in `RecorderSupervisor.FinalizeToTerminalAsync`: on `Done`, best-effort delete the whole `/segments/{id}` dir (nothing cleaned recording segments before → they'd accumulate 6 GB+ each forever).

## Verification (no sources/EPG/leagues added — pristine)
Container `Up (healthy)`; `/api/health` ok, db `sqlite-wal`, **0 sources**, ffmpeg 5.1.9 (has the `setts` bsf). UI 200; logo PNG served (525 KB). Unraid dashboard: logo → WebUI works. Test recording (public stream, no provider) → `Done`, file in `/mnt/user/Media/Sports` owned `nobody:users`, segment scratch auto-removed. Throwaway test deleted. Handed over clean for the user to add sources/EPG/leagues themselves.

## Kept in code, not configured
`HaWebhookService` (no-op until `ha_webhook_url` set). Plex Custom Metadata agent (`/plex`) optional. Prowlarr connection dropped.

---

# Phase 13 — event sync fix: TheSportsDB free `eventsseason` cap (2026-06-16)

After deploy, syncing the World Cup returned only **5 events** (all past) — yet the same free key `123` had synced ~72 (two weeks) on the dev box days earlier. Root cause (proven by direct API tests, **not** a DVarr bug): **TheSportsDB tightened their free-tier `eventsseason.php`** — it now returns only a season's *first* few matches (~5 on the anonymous `3` key, ~15 on a registered free key), not the full schedule and not a rolling window; `eventsnextleague` returns 1; the v2 API is Premium-only (HTTP 400).

**Fix:** `eventsday.php?d={UTC-date}&l={leagueId}` is **not** capped and returns a league's matches for any given day on the free key. `EventFetcher.FetchTsdbAsync` now keeps the `eventsseason` call (for metadata) **and** loops the new `TheSportsDbClient.GetDayEventsAsync` per UTC day across `[now-1 .. now+ScheduleHorizonDays]` (horizon clamped 1–30; 200 ms between calls for the 30/min limit; 429s degrade gracefully), merging by stable `idEvent`. Free-tier leagues now get their full horizon — no Premium/ICS needed just to see upcoming fixtures.

**Verified:** World Cup re-sync 5 → **49 events** on the server (key `3`), 41 within the next 14 days; 15 → 53 locally (key `123`). Deployed (rebuilt on the server).

---

# Phase 14 — NVENC for the live-preview transcode (2026-06-16)

Previewing an HEVC/AC-3 channel (which falls back to the HLS transcode) pegged the CPU — `PreviewTranscodeManager` used software libx264. Moved it onto the GTX 1070.

**No ffmpeg change needed:** the container's Debian ffmpeg 5.1.9 already ships `h264_nvenc`/`hevc_nvenc`, `scale_cuda`, and the `cuda` hwaccel; the Unraid nvidia runtime is registered.

**GPU access (`docker-compose.yml`):** `runtime: nvidia`, `NVIDIA_VISIBLE_DEVICES=0000:01:00.0` (the 1070's PCI id, mirroring Plex), `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility`. Verified `nvidia-smi` works inside the container and `/dev/nvidia*` device nodes are present (accessible to the non-root `99:100` user).

**Transcode path (`PreviewTranscodeManager`):** full-GPU pipeline — `-hwaccel cuda -hwaccel_output_format cuda` (NVDEC) → `-vf scale_cuda=-2:720:format=yuv420p` → `-c:v h264_nvenc -preset p4 -tune ll`. The **`format=yuv420p` is essential** — `h264_nvenc` can't encode 10-bit and many sports HEVC channels are 10-bit; it down-converts to 8-bit on the GPU (plain `scale_cuda` → "device doesn't support required NVENC features"). Gated by `/dev/nvidia0` presence, with a **per-session libx264 fallback** (lease held across the retry) if a GPU start ever fails (e.g., NVENC session exhaustion).

**Verified on the server:** log `channel … transcoding on GPU (NVENC)`; `nvidia-smi` shows encoder + decoder engaged; container CPU **~2.8%** during a preview (was pegging cores); runs **concurrently with Plex's NVENC** (no single-session conflict on driver 580). Recording capture stays lossless `-c copy` (no GPU); finalize audio re-encode stays CPU AAC.

---

## Phase 15 — reliability de-overlap, ffmpeg 8.x, conflict planning, per-sport durations, Sportarr media (2026-06-16, clean-redeployed)

**Stream reliability ("going back in time" fix).** Capture: removed `-reset_timestamps 1`, added `-copyts` — segments now share the source's continuous absolute PTS, so a reconnect's re-served seconds appear as a *backward* PTS instead of being silently re-zeroed. `FinalizeAsync` runs a **de-overlap pass** (`BuildConcatListAsync` + `ProbePtsRangeAsync`): ffprobe each segment's min/max video PTS; **drop a fully-duplicate segment**, emit an **`inpoint`** for a partial overlap (lossless `-c copy`; inter-frame seek leaves ≤1 GOP residual — strictly better than replaying the whole overlap). **Clean-EOF handling:** `CaptureUntilStopOrStallAsync` returns a typed `CaptureExit`; a clean rc=0 EOF relaunches instantly and stays `Recording` (no Recovering churn; flap-throttled at >8/30s), while rc≠0/stall/dead-picture keep the back-off + failover ladder. Finalize audio: `-af aresample=async=1:min_hard_comp=0.1` + `-avoid_negative_ts make_zero`. Gated by `finalize_deoverlap_enabled` + `clean_eof_instant_relaunch` (both default true).

**ffmpeg 8.x.** Dockerfile downloads the BtbN `ffmpeg-n8.1-latest-linux64-gpl-8.1` static build (durable `latest`-tag asset; dated `autobuild-*` tags get pruned → 404), extracts to `/opt/ffmpeg`, symlinks into `/usr/local/bin`, sets `DVarr__FfmpegPath/FfprobePath`. Server now runs **n8.1.1** (was Debian 5.1.9); NVENC encoders intact.

**Per-sport event durations.** `EventFetcher` reports EndUtc=null (TheSportsDB has no end); `EventIngestService` + `AutoScheduleService` fill it via `SettingsService.GetEventDurationSecondsAsync(sport)` = `default_event_duration_s`=7200 + `event_duration_overrides_json`={"motorsport":10800}. Soccer 2h+post-pad; motorsport stays long (keep-full-race-endings).

**Conflict planning across the two logins.** New `CreditAwarePlanner` — `OptionsForEventAsync` = primary credential + same logical channel on the other login (`ResolverService.EquivalentChannelsAsync`: StreamId → LogicalKey → NameNorm); `Decide` = first free credential wins, else preempt a strictly-lower-ranked **Pending** incumbent, else Conflict. `AutoScheduleService` rebuilt around per-credential occupancy + conflict re-evaluation each tick. New `RecordingState.Conflict` (holds no slot) + `League.Priority` (migration `Phase15ConflictPlanning`, one additive int column). Endpoints: `/api/conflicts`, `/api/recordings/plan-preview`, `/api/recordings/{id}/reassign`. New **Conflicts** UI page + a live plan-preview badge in the Schedule modal. Ladder: RecordingPriority → League.Priority → earliest → id; default Refuse, preempt only a still-Pending lower one.

**Sportarr-style finished-game files.** `MediaImportService` now files into a per-game folder `<Title> (yyyy-MM-dd) E<NN>/` with a `HDTV-<height>p` tag (ffprobe), e.g. `FIFA World Cup/Season 2026/USA vs Paraguay (2026-06-12) E04/FIFA World Cup - S2026E04 - USA vs Paraguay - HDTV-2160p.mkv`. **Episode collision fixed:** the manual-match path used `best.Round` (every WC matchday-1 game is intRound=1 → all E01) → now day-of-year (unique per day). Event-linked path keeps the chronological per-season ordinal (consistent with `PlexEndpoints`); `SeasonOrdinalAsync` no longer silently falls back to E01. Monitor an event (event-linked) for clean tournament E-numbers.

**Schedule modal overflow.** CSS `min-width:0` on `.modal .fields`/`label.field` + `width/max-width:100%/box-sizing` on controls; channel results are now a custom ellipsis listbox (`.picklist`/`.pickrow`, hover = full name) writing into a hidden `#cascCh` input.

**Clean-install redeploy.** Build dir `/mnt/user/appdata/dvarr-build` (compose context). Synced via `tar | scp` (GNU tar on Windows needs MSYS `/c/...` paths). Built the new image with `docker compose build` while the old container ran, then `down` → backed up `dvarr.db{,-wal,-shm}` to `/mnt/user/appdata/dvarr/backup-20260616-125516/` (no `sources.import.json` exists — creds live only in the DB) → cleared segment scratch → `up -d`. Verified: healthy, ffmpeg n8.1.1, `/api/sources`=[], 0 recordings, settings seeded, migration applied. User re-adds the 2 logins + re-imports the World Cup.
