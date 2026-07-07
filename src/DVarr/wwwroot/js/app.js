'use strict';
// DVarr UI — multi-page SPA (sidebar nav, hash routing) over the DVarr REST + SSE API.

const $ = (s, r = document) => r.querySelector(s);
// Tolerant of empty / non-JSON bodies (e.g. a 404 NotFound has no body) so handlers never throw on .json().
async function _json(res) {
  // Session/basic auth gone (e.g. PWA launched from the cached shell, cookie expired): the first API call 401s.
  // Bounce to the login page so the user can re-establish a trusted-device session. Guard against a redirect loop
  // if we're somehow already on login.html.
  if (res.status === 401 && !location.pathname.endsWith('/login.html')) {
    location.replace('/login.html');
    return {};
  }
  const t = await res.text();
  let body; try { body = t ? JSON.parse(t) : {}; } catch { body = { _raw: t }; }
  // Surface non-2xx responses as an .error even when the body is empty/has no error field, so callers that check
  // r.error never mistake a 4xx/5xx (or empty 404) for success.
  if (!res.ok && body && typeof body === 'object' && body.error == null) body.error = body._raw || `request failed (${res.status})`;
  return body;
}
const api = {
  get: async p => _json(await fetch(p)),
  post: async (p, b) => _json(await fetch(p, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: b ? JSON.stringify(b) : undefined })),
  put: async (p, b) => _json(await fetch(p, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b) })),
  del: async p => _json(await fetch(p, { method: 'DELETE' })),
};
const esc = s => (s == null ? '' : String(s)).replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));
// For a value embedded in a single-quoted JS string inside an HTML attribute: onclick="fn('${jsq(name)}')".
// JS-escape \\ and ' first (the browser HTML-decodes the attribute BEFORE the JS runs), then HTML-escape &"<>.
const jsq = s => esc(String(s == null ? '' : s).replace(/\\/g, '\\\\').replace(/'/g, "\\'"));
const brisbane = e => e ? new Date(e * 1000).toLocaleString('en-AU', { timeZone: 'Australia/Brisbane', day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }) : '—';
const hhmm = e => e ? new Date(e * 1000).toLocaleString('en-AU', { timeZone: 'Australia/Brisbane', hour: '2-digit', minute: '2-digit', hour12: false }) : '';
const mb = b => b > 0 ? (b / 1e6).toFixed(1) + ' MB' : '—';
const sc = s => 's-' + (s || '').toLowerCase();
// Keyword (token) matching: "uk sports" matches "UK| Sports" and "Sky Sports UK".
const norm = s => (s == null ? '' : String(s)).toLowerCase().replace(/[^a-z0-9 ]+/g, ' ').replace(/\s+/g, ' ').trim();
const tokensMatch = (hay, q) => { const n = norm(hay); return norm(q).split(' ').filter(Boolean).every(t => n.includes(t)); };
// Epoch → value for a <input type=datetime-local> in the browser's local zone.
const toLocalInput = e => new Date((e * 1000) - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16);
const nowLocalInput = () => toLocalInput(Math.floor(Date.now() / 1000));

// 10-colour palette for league calendar cards — hues spread around the wheel so they're easy to tell apart.
const LEAGUE_COLORS = ['#e6194b', '#f58231', '#ffd60a', '#84cc16', '#15803d', '#06b6d4', '#2563eb', '#7c3aed', '#db2777', '#78350f'];
// Only accept a strict #rrggbb hex — never inject an arbitrary stored string into a style attribute (XSS guard).
const okColor = c => /^#[0-9a-fA-F]{6}$/.test(c || '');
// A league's colour: its chosen (validated) one, else a stable default derived from its id.
const leagueColor = l => (l && okColor(l.color)) ? l.color : LEAGUE_COLORS[((l && l.id || 0)) % LEAGUE_COLORS.length];
// Black or white text for contrast on a hex background.
const textOn = hex => { const c = (hex || '#000').replace('#', ''); const r = parseInt(c.substr(0, 2), 16) || 0, g = parseInt(c.substr(2, 2), 16) || 0, b = parseInt(c.substr(4, 2), 16) || 0; return (0.299 * r + 0.587 * g + 0.114 * b) > 150 ? '#0c0f14' : '#fff'; };
// Brisbane (fixed UTC+10) helpers for the month calendar.
const BNE_OFFSET = 10 * 3600;
const bneDayKey = epochSec => Math.floor((epochSec + BNE_OFFSET) / 86400);            // unique day number in Brisbane
const bneCellKey = (y, m, d) => Math.floor(Date.UTC(y, m, d) / 86400000);              // same scale for a calendar cell
const bneMonthStart = (y, m) => Math.floor(Date.UTC(y, m, 1) / 1000) - BNE_OFFSET;     // epoch of Brisbane 1st 00:00
const bneParts = epochSec => { const p = {}; new Intl.DateTimeFormat('en-AU', { timeZone: 'Australia/Brisbane', year: 'numeric', month: 'numeric', day: 'numeric' }).formatToParts(new Date(epochSec * 1000)).forEach(x => p[x.type] = x.value); return { y: +p.year, m: +p.month - 1, d: +p.day }; };

const ACTIVE = ['Starting', 'Recording', 'Recovering', 'FailingOver', 'Degraded', 'Stopping', 'Finalizing'];
// Terminal/finished states for the dashboard "Recently completed" panel (newest first).
const DASH_TERMINAL = ['Done', 'NeedsAttention', 'Missed', 'Cancelled'];

// ---- icons (feather-style) ----
const I = {
  dashboard: '<svg viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="9"/><rect x="14" y="3" width="7" height="5"/><rect x="14" y="12" width="7" height="9"/><rect x="3" y="16" width="7" height="5"/></svg>',
  recordings: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="3" fill="currentColor" stroke="none"/></svg>',
  channels: '<svg viewBox="0 0 24 24"><rect x="2" y="7" width="20" height="14" rx="2"/><path d="M7 7l5-4 5 4"/></svg>',
  guide: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/></svg>',
  calendar: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18M8 14h.01M12 14h.01M16 14h.01M8 18h.01M12 18h.01"/></svg>',
  leagues: '<svg viewBox="0 0 24 24"><path d="M6 9H4.5a2.5 2.5 0 0 1 0-5H6M18 9h1.5a2.5 2.5 0 0 0 0-5H18M4 22h16M10 14.66V17c0 .55-.47.98-.97 1.21L7 19M14 14.66V17c0 .55.47.98.97 1.21L17 19M6 2h12v7a6 6 0 0 1-12 0V2z"/></svg>',
  sources: '<svg viewBox="0 0 24 24"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5"/><path d="M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/></svg>',
  activity: '<svg viewBox="0 0 24 24"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>',
  settings: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-2.7 1.1V21a2 2 0 1 1-4 0v-.1A1.6 1.6 0 0 0 7 19.4a1.6 1.6 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1A1.6 1.6 0 0 0 3 14a2 2 0 1 1 0-4h.1A1.6 1.6 0 0 0 4.6 7l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1A1.6 1.6 0 0 0 10 4.6 2 2 0 1 1 14 4.6 1.6 1.6 0 0 0 17 4.6l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0 1.1 2.7H21a2 2 0 1 1 0 4z"/></svg>',
  play: '<svg viewBox="0 0 24 24"><polygon points="6 4 20 12 6 20" fill="currentColor" stroke="none"/></svg>',
  plus: '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>',
  refresh: '<svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/></svg>',
  conflicts: '<svg viewBox="0 0 24 24"><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/></svg>',
  clock: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="M20 6 9 17l-5-5"/></svg>',
  layers: '<svg viewBox="0 0 24 24"><path d="M12 2 2 7l10 5 10-5-10-5z"/><path d="M2 12l10 5 10-5"/><path d="M2 17l10 5 10-5"/></svg>',
  dots: '<svg viewBox="0 0 24 24"><circle cx="12" cy="5" r="2" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="2" fill="currentColor" stroke="none"/><circle cx="12" cy="19" r="2" fill="currentColor" stroke="none"/></svg>',
};

const NAV = [
  { id: 'dashboard', label: 'Dashboard' },
  { id: 'recordings', label: 'Recordings' },
  { id: 'conflicts', label: 'Conflicts' },
  { id: 'calendar', label: 'Calendar' },
  { id: 'leagues', label: 'Leagues' },
  { id: 'channels', label: 'Channels' },
  { id: 'guide', label: 'Guide' },
  { id: 'sources', label: 'Sources' },
  { id: 'activity', label: 'Activity' },
  { id: 'settings', label: 'Settings' },
];

// ---- toast / modal ----
function toast(msg, kind = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + kind;
  t.textContent = msg;
  $('#toastRoot').appendChild(t);
  setTimeout(() => t.remove(), 4200);
}
function modal(html, width) {
  const bg = document.createElement('div');
  bg.className = 'modal-bg';
  // .modal-x: close button for the phone full-screen sheet (display:none on desktop, so wide layouts are untouched).
  bg.innerHTML = `<div class="modal"${width ? ` style="width:${width}"` : ''}>${html}<button type="button" class="modal-x" aria-label="Close" onclick="closeModals()"><svg viewBox="0 0 24 24" style="width:16px;height:16px;stroke:currentColor;fill:none;stroke-width:2"><path d="M6 6l12 12M18 6L6 18"/></svg></button></div>`;
  bg.addEventListener('click', e => { if (e.target === bg) closeModals(); });
  $('#modalRoot').appendChild(bg);
  return bg;
}
function closeModals() { stopPreview(); $('#modalRoot').replaceChildren(); }

// Ko-fi donation panel in a modal (official embed URL). The iframe only loads when opened, and the
// sidebar link's href stays a plain Ko-fi URL so it still works if JS is broken. Returns false to
// swallow the anchor's default navigation.
function donate() {
  modal(`<h2>🍺 Buy me a beer for the next game</h2>
    <iframe class="kofi-frame" src="https://ko-fi.com/haydenw22/?hidefeeditems=true&widget=true&embed=true" title="Support DVarr on Ko-fi" loading="lazy"></iframe>
    <div class="note" style="margin-top:12px">Panel not loading? <a href="https://ko-fi.com/haydenw22" target="_blank" rel="noopener">Open Ko-fi in a new tab ↗</a></div>`, 'min(420px,94vw)');
  return false;
}

// Copy text to the clipboard, toasting success/failure. Prefers the async Clipboard API; falls back to selecting a
// (possibly off-screen) input + execCommand('copy') for older WebViews / non-secure contexts where clipboard is absent.
// `srcSel` is an optional CSS selector of an existing <input> to select() for the fallback (avoids a flash).
function copyText(text, srcSel) {
  const done = () => toast('Copied to clipboard', 'ok');
  const fallback = () => {
    let el = srcSel && $(srcSel), temp = null;
    if (!el) { temp = document.createElement('input'); temp.value = text; temp.style.position = 'fixed'; temp.style.opacity = '0'; document.body.appendChild(temp); el = temp; }
    el.focus(); el.select();
    let ok = false; try { ok = document.execCommand('copy'); } catch { ok = false; }
    if (temp) temp.remove();
    ok ? done() : toast("Couldn't copy — select the text and copy it manually", 'err');
  };
  if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(done, fallback);
  else fallback();
}

// ---- mobile drawer nav (sidebar slides in over a scrim below ~820px) ----
// body.drawer-locked freezes page scroll behind the open drawer (class is only ever set on small screens).
function closeDrawer() { const a = $('.app'); if (a) a.classList.remove('drawer-open'); document.body.classList.remove('drawer-locked'); const h = $('#hamburger'); if (h) h.setAttribute('aria-expanded', 'false'); }
function toggleDrawer() { const a = $('.app'); if (!a) return; const open = a.classList.toggle('drawer-open'); document.body.classList.toggle('drawer-locked', open); const h = $('#hamburger'); if (h) h.setAttribute('aria-expanded', open ? 'true' : 'false'); }

// ---- kebab "⋯" action menu (phone) ----
// One reusable dropdown per row: items = [{ label, fn, danger?, title? }] where `fn` is an inline-JS string that
// mirrors the row's desktop button (callers jsq()-escape embedded values exactly as they do for those buttons).
// The whole component is display:none on desktop (CSS), so wide layouts keep their inline buttons unchanged.
function kebab(items) {
  const its = items.filter(Boolean).map(i =>
    `<button type="button" class="kebab-item${i.danger ? ' danger' : ''}" role="menuitem"${i.title ? ` title="${esc(i.title)}"` : ''} onclick="${i.fn}">${i.label}</button>`).join('');
  return `<span class="kebab-wrap"><button type="button" class="kebab-btn" aria-haspopup="true" aria-expanded="false" aria-label="More actions" onclick="toggleKebab(this)">${I.dots}</button><div class="kebab-menu" role="menu">${its}</div></span>`;
}
let _kebabOpen = false; // cheap guard so the capture-phase scroll listener doesn't touch the DOM when nothing is open
function closeKebabs() {
  if (!_kebabOpen) return;
  _kebabOpen = false;
  document.querySelectorAll('.kebab-menu.open').forEach(m => { m.classList.remove('open'); m.style.left = m.style.top = ''; });
  document.querySelectorAll('.kebab-btn[aria-expanded="true"]').forEach(b => b.setAttribute('aria-expanded', 'false'));
}
function toggleKebab(btn) {
  const menu = btn.nextElementSibling; if (!menu) return;
  const wasOpen = menu.classList.contains('open');
  closeKebabs(); // only one open at a time
  if (wasOpen) return;
  menu.classList.add('open');
  btn.setAttribute('aria-expanded', 'true');
  _kebabOpen = true;
  // Fixed-position the dropdown inside the viewport: right-align to the button; flip above it when there's no room below.
  const r = btn.getBoundingClientRect();
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  const left = Math.max(8, Math.min(r.right - mw, window.innerWidth - mw - 8));
  let top = r.bottom + 6;
  if (top + mh > window.innerHeight - 8) top = Math.max(8, r.top - mh - 6);
  menu.style.left = left + 'px'; menu.style.top = top + 'px';
}
// Outside tap or choosing an item closes the menu (an item's own inline onclick has already run by the time this
// bubbles to the document). Any scroll or resize also closes it, since the menu is fixed-positioned.
document.addEventListener('click', e => { if (!e.target.closest('.kebab-btn')) closeKebabs(); });
window.addEventListener('scroll', closeKebabs, true);
window.addEventListener('resize', closeKebabs);

// ---- live refresh wiring ----
let liveRefresh = null, liveTimer = null;
function setLive(fn) { liveRefresh = fn; }
function connectSSE() {
  const es = new EventSource('/api/stream/recordings');
  // Swallow a stale/failed refresh so a transient API error (or a draw() against a just-navigated page) can't throw an
  // unhandled rejection and freeze live updates on stale data.
  es.onmessage = () => { clearTimeout(liveTimer); liveTimer = setTimeout(() => { try { const p = liveRefresh && liveRefresh(); if (p && p.catch) p.catch(() => {}); } catch {} }, 150); };
  es.onerror = () => { es.close(); setTimeout(connectSSE, 3000); };
}

// ---- header / nav state ----
async function pollHealth() {
  try {
    const h = await api.get('/api/health');
    $('#stSlots').textContent = `${h.sources.free_credentials} / ${h.sources.total} free`;
    $('#stClock').textContent = h.time.brisbane.replace(/:\d\d /, ' ');
    // topbar chips (same poll as before — no extra requests): slots dot + database health
    const sd = $('#stSlotsDot'); if (sd) sd.className = 'dot ' + (h.sources.free_credentials > 0 ? 'ok' : 'warn');
    const dd = $('#stDbDot'); if (dd) dd.className = 'dot ' + (h.db.ok ? 'ok' : 'bad');
    const dt = $('#stDbTxt'); if (dt) dt.textContent = 'Database ' + (h.db.ok ? 'OK' : 'down');
    $('#footDot').className = 'dot ' + (h.db.ok ? 'ok' : 'bad');
    $('#footTxt').textContent = `v${(h.version || '0').split('.').slice(0, 3).join('.')} · db ${h.db.ok ? 'ok' : 'down'}`;
    const badge = $('#menu .nav-item[data-route="recordings"] .nav-badge');
    if (badge) {
      if (h.recordings.active > 0) { badge.textContent = h.recordings.active; badge.className = 'nav-badge live'; badge.style.display = ''; }
      else { badge.style.display = 'none'; }
    }
  } catch {
    $('#footDot').className = 'dot bad'; $('#footTxt').textContent = 'offline';
    const sd = $('#stSlotsDot'); if (sd) sd.className = 'dot bad';
    const dd = $('#stDbDot'); if (dd) dd.className = 'dot bad';
    const dt = $('#stDbTxt'); if (dt) dt.textContent = 'Database ?';
  }
}

function buildNav() {
  $('#menu').innerHTML = NAV.map(n =>
    `<a class="nav-item" data-route="${n.id}" href="#/${n.id}">${I[n.id]}<span>${n.label}</span>${n.id === 'recordings' ? '<span class="nav-badge live" style="display:none"></span>' : ''}</a>`
  ).join('');
}

// =========================================================================
// PAGES
// =========================================================================
const PAGES = {};

// ---- Dashboard (at-a-glance: recordings live+scheduled, upcoming events, leagues) ----
PAGES.dashboard = {
  title: 'Dashboard',
  actions: () => `<button onclick="openScheduleModal()">${I.plus} Schedule</button><button class="ghost" onclick="openTestModal()">${I.play} Test</button>`,
  menuActions: [{ label: 'Test recording', fn: 'openTestModal()' }], // phone topbar ⋯ (mirrors the ghost button above)
  async render(el) {
    const draw = async () => {
      const now = Math.floor(Date.now() / 1000);
      const [health, recs, leagues, events, sources] = await Promise.all([
        api.get('/api/health').catch(() => null),
        api.get('/api/recordings'),
        api.get('/api/leagues'),
        api.get(`/api/events?from=${now}`),
        api.get('/api/sources').catch(() => []),
      ]);
      if (!Array.isArray(recs) || !Array.isArray(leagues) || !Array.isArray(events)) return; // transient API error — keep the current view, retry next tick
      const live = recs.filter(r => ACTIVE.includes(r.state));
      const scheduled = recs.filter(r => r.state === 'Pending' && r.startUtc <= now + 86400).sort((a, b) => a.startUtc - b.startUtc); // next 24h only
      const upcoming = events.filter(e => e.start <= now + 86400).sort((a, b) => a.start - b.start); // next 24h only
      const completed = recs.filter(r => DASH_TERMINAL.includes(r.state)).sort((a, b) => (b.endUtc || b.startUtc) - (a.endUtc || a.startUtc)).slice(0, 6);
      const srcList = Array.isArray(sources) ? sources : [];
      // openCalEvent (row click in "Next 24 hours") reads window._calEvents — populate it from this fetch too.
      window._calEvents = window._calEvents || {}; events.forEach(e => { window._calEvents[e.id] = e; });
      // KPI row + responsive panel grid (single column on a phone, 2–3 across on desktop).
      el.innerHTML = `
        ${statRow(health, live.length, scheduled.length)}
        <div class="dash-grid">
          ${dashPanel({ icon: I.recordings, title: 'Recording now', count: live.length, link: '#/recordings', body: live.length ? dashRecList(live, leagues) : radarEmpty() })}
          ${dashPanel({ icon: I.clock, title: 'Scheduled — next 24h', count: scheduled.length, link: '#/recordings', body: scheduled.length ? dashRecList(scheduled, leagues) : emptyBox('Nothing scheduled in the next 24 hours.') })}
          ${dashPanel({ icon: I.check, title: 'Recently completed', count: completed.length, link: '#/recordings', body: completed.length ? completedTable(completed, leagues) : emptyBox('No finished recordings yet.') })}
          ${dashPanel({ icon: I.sources, title: 'Sources', count: srcList.length, body: srcList.length ? sourcesPanel(srcList) : emptyBox('No sources yet — add one on the Sources page.'), foot: { href: '#/sources', label: 'Manage sources' } })}
          ${dashPanel({ icon: I.calendar, title: 'Next 24 hours', count: upcoming.length, body: upcoming.length ? upcomingEvents(upcoming, leagues) : emptyBox('No monitored events in the next 24 hours.'), foot: { href: '#/calendar', label: 'View full schedule' } })}
          ${dashPanel({ icon: I.leagues, title: 'Leagues', count: leagues.length, body: leagues.length ? leagueChips(leagues) : emptyBox('No leagues yet — add one on the Leagues page.'), foot: { href: '#/leagues', label: 'Browse all leagues' } })}
        </div>`;
    };
    await draw();
    setLive(draw);
  },
};
// KPI stat cards across the top of the dashboard (icon chip + label + value + subcaption + accent underline).
function statRow(h, liveN, schedN) {
  const kpi = (cls, icon, label, val, sub) => `<div class="kpi${cls ? ' ' + cls : ''}"><span class="kpi-ic">${icon}</span><div class="kpi-meta"><div class="kpi-label">${esc(label)}</div><div class="kpi-value">${val}</div><div class="kpi-sub">${esc(sub)}</div></div></div>`;
  const slots = h && h.sources ? `${h.sources.free_credentials}<small> / ${h.sources.total}</small>` : '—';
  const dbOk = h && h.db ? !!h.db.ok : null;
  return `<div class="kpi-row">
    ${kpi('', I.recordings, 'Recording now', liveN, liveN ? 'live capture in progress' : 'idle')}
    ${kpi('', I.clock, 'Scheduled · 24h', schedN, 'in the next 24 hours')}
    ${kpi('', I.layers, 'Free slots', slots, 'provider stream slots')}
    ${kpi(dbOk == null ? '' : dbOk ? 'ok' : 'bad', I.check, 'Database', dbOk == null ? '—' : dbOk ? 'OK' : 'DOWN', dbOk == null ? 'no health data' : dbOk ? 'storage healthy' : 'check the server')}</div>`;
}
// Panel card: header (icon + uppercase title + count pill + "View all") over a list body, optional footer link.
function dashPanel(o) {
  return `<div class="panel dash-cell">
    <div class="panel-head">${o.icon || ''}<span class="panel-title">${esc(o.title)}</span>${o.count ? `<span class="count-pill">${o.count}</span>` : ''}${o.link ? `<a class="panel-link" href="${o.link}">View all</a>` : ''}</div>
    <div class="panel-body">${o.body}</div>
    ${o.foot ? `<a class="panel-foot" href="${o.foot.href}">${esc(o.foot.label)} →</a>` : ''}
  </div>`;
}
// Pure-CSS radar (concentric circles) empty state for the "Recording now" panel.
function radarEmpty() {
  return `<div class="radar-empty"><div class="radar"></div><b>Nothing recording right now.</b><span>You're all caught up!</span></div>`;
}
// 44px icon chip for a list row: league poster when we know it, else a tinted monogram.
function rowChip(r, leagues) {
  const lg = (leagues || []).find(l => l.id === r.leagueId);
  if (lg && lg.poster) return `<span class="prow-ic"><img src="${esc(lg.poster)}" alt="" loading="lazy"/></span>`;
  const c = lg ? leagueColor(lg) : '#3b82f6'; // leagueColor is #rrggbb-validated → safe in a style attr
  const ch = String(r.league || r.title || '?').trim().charAt(0).toUpperCase() || '?';
  return `<span class="prow-ic prow-mono" style="background:${c}22;color:${c};border-color:${c}44">${esc(ch)}</span>`;
}
// Compact recording rows for the dashboard panels — narrow-friendly (title + when + state, with stop for live ones).
// The full management table with all actions lives on the Recordings page; tap a row to go there.
function dashRecList(rows, leagues) {
  return `<div class="dash-rec">${rows.map(r => {
    const active = ACTIVE.includes(r.state);
    return `<div class="prow clickrow" onclick="location.hash='#/recordings'">
      ${rowChip(r, leagues)}
      <div class="prow-main"><b>${esc(r.title)}</b><div class="prow-sub">${brisbane(r.startUtc)}${r.channel ? ' · ' + esc(r.channel) : ''}</div></div>
      <div class="prow-side"><span class="pill ${sc(r.state)}">${r.state}</span>${active ? `<button class="ghost sm" onclick="event.stopPropagation();stopRec(${r.id})">stop</button>` : ''}</div>
    </div>`;
  }).join('')}</div>`;
}
// Recently-finished recordings (Done/Missed/NeedsAttention/Cancelled) — size in MB + state pill; tap → Recordings page.
function completedTable(rows, leagues) {
  return `<div class="dash-rec">${rows.map(r => `
    <div class="prow clickrow" onclick="location.hash='#/recordings'">
      ${rowChip(r, leagues)}
      <div class="prow-main"><b>${esc(r.title)}</b><div class="prow-sub">${brisbane(r.endUtc || r.startUtc)} · ${mb(r.bytesWritten)}</div></div>
      <div class="prow-side"><span class="pill ${sc(r.state)}">${r.state}</span></div>
    </div>`).join('')}</div>`;
}
// Available sources with one-tap EPG refresh + ingest, right on the dashboard.
function sourcesPanel(sources) {
  return `<div class="dash-rec">${sources.map(x => `
    <div class="prow">
      <div class="prow-main"><b>${esc(x.label)}</b><div class="prow-sub">${x.channels} ch · ${x.programmes || 0} epg · <span style="color:${x.slotFree ? 'var(--ok)' : 'var(--warn)'}">${x.slotFree ? 'slot free' : 'slot busy'}</span></div></div>
      <div class="prow-side acts-row">
        <button class="ghost sm" onclick="syncEpg(${x.id},'${jsq(x.label)}')" title="Refresh this source's EPG">${I.refresh} EPG</button>
        <button class="ghost sm" onclick="ingest(${x.id},'${jsq(x.label)}')" title="Re-ingest this source's channels">Ingest</button>
        ${kebab([
          { label: 'Refresh EPG', fn: `syncEpg(${x.id},'${jsq(x.label)}')` },
          { label: 'Ingest channels', fn: `ingest(${x.id},'${jsq(x.label)}')` },
        ])}
      </div>
    </div>`).join('')}</div>`;
}

// ---- Recordings ----
PAGES.recordings = {
  title: 'Recordings',
  actions: () => `<button onclick="openScheduleModal()">${I.plus} Schedule</button><button class="ghost" onclick="openTestModal()">${I.play} Test</button>`,
  menuActions: [{ label: 'Test recording', fn: 'openTestModal()' }], // phone topbar ⋯ (mirrors the ghost button above)
  async render(el) {
    el.innerHTML = `<div class="toolbar">
        <select id="recFilter"><option value="">All states</option><option>Recording</option><option>Pending</option><option>Done</option><option>NeedsAttention</option><option>Missed</option></select>
        <select id="recLeague"><option value="">All leagues</option></select>
        <span class="muted" id="recCount"></span></div>
      <div id="recTableWrap"></div>`;
    const draw = async () => {
      const recs = await api.get('/api/recordings');
      if (!Array.isArray(recs)) return; // transient API error — don't blow away the table on a failed refresh
      // League filter options — distinct leagues present in the loaded rows (null/manual grouped separately).
      // Rebuilt on every refresh (SSE) but the current selection is preserved.
      const lSel = $('#recLeague'); const prevLeague = lSel.value;
      const seen = new Map(); let hasManual = false;
      recs.forEach(r => {
        if (r.leagueId != null) { const k = String(r.leagueId); if (!seen.has(k)) seen.set(k, r.league || ('League #' + k)); }
        else hasManual = true;
      });
      lSel.innerHTML = `<option value="">All leagues</option>`
        + [...seen.entries()].sort((a, b) => a[1].localeCompare(b[1])).map(([id, name]) => `<option value="${esc(id)}">${esc(name)}</option>`).join('')
        + (hasManual ? `<option value="manual">Manual / no league</option>` : '');
      if ([...lSel.options].some(o => o.value === prevLeague)) lSel.value = prevLeague;
      const f = $('#recFilter').value, lf = lSel.value;
      let rows = f === 'Recording' ? recs.filter(r => ACTIVE.includes(r.state)) : (f ? recs.filter(r => r.state === f) : recs);
      if (lf === 'manual') rows = rows.filter(r => r.leagueId == null);
      else if (lf) rows = rows.filter(r => String(r.leagueId) === lf);
      $('#recCount').textContent = `${rows.length} recording${rows.length === 1 ? '' : 's'}`;
      $('#recTableWrap').innerHTML = rows.length ? recTable(rows, true) : emptyBox('No recordings yet. Use “Schedule” or “Test”.');
    };
    $('#recFilter').addEventListener('change', draw);
    $('#recLeague').addEventListener('change', draw);
    await draw();
    setLive(draw);
  },
};

function recTable(rows, withActions) {
  return `<table class="rtable"><thead><tr><th>Title</th><th>State</th><th>Channel</th><th>Source</th><th>Size</th><th>Window (Brisbane)</th>${withActions ? '<th></th>' : ''}</tr></thead><tbody>${rows.map(r => {
    // Same conditions as the inline desktop buttons — the phone kebab mirrors them 1:1 so no action is lost.
    const pendingish = r.state === 'Pending' || r.state === 'Conflict';
    const stoppable = ACTIVE.includes(r.state) || pendingish;
    const importable = r.state === 'Done' && (r.outputPath || '').includes('.unsorted');
    return `
    <tr><td data-label="">${esc(r.title)}</td>
      <td data-label="State"><span class="pill ${sc(r.state)}">${r.state}</span>${r.attemptCount ? ` <span class="muted" title="relaunch/failover attempts">↻${r.attemptCount}</span>` : ''}</td>
      <td data-label="Channel">${esc(r.channel)}</td><td data-label="Source" class="muted">${esc(r.source)}</td>
      <td data-label="Size" class="mono">${mb(r.bytesWritten)}</td>
      <td data-label="Window" class="mono muted">${brisbane(r.startUtc)} – ${brisbane(r.endUtc)}</td>
      ${withActions ? `<td data-label="" class="row acts" style="gap:6px">${pendingish ? `<button class="sm" onclick="startRec(${r.id})" title="Start this recording now (early/manual)">start</button><button class="ghost sm" onclick="reresolveRec(${r.id})" title="Re-resolve the channel from the league's current mapping">re-resolve</button>` : ''}${stoppable ? `<button class="ghost sm" onclick="stopRec(${r.id})">stop</button>` : ''}${importable ? `<button class="sm" onclick="openImportModal(${r.id}, ${r.startUtc}, '${jsq(r.title || '')}')" title="Sort this manual recording into the library">import</button>` : ''}<button class="danger sm" onclick="delRec(${r.id})">delete</button>${kebab([
        pendingish && { label: 'Start now', fn: `startRec(${r.id})` },
        pendingish && { label: 'Re-resolve channel', fn: `reresolveRec(${r.id})` },
        stoppable && { label: 'Stop', fn: `stopRec(${r.id})` },
        importable && { label: 'Import into library', fn: `openImportModal(${r.id}, ${r.startUtc}, '${jsq(r.title || '')}')` },
        { label: 'Delete', fn: `delRec(${r.id})`, danger: true },
      ])}</td>` : ''}
    </tr>`;
  }).join('')}</tbody></table>`;
}
function notesList(notes) {
  const col = s => s === 'Critical' ? 'var(--crit)' : s === 'Warn' ? 'var(--warn)' : 'var(--dim)';
  return `<table class="rtable"><tbody>${notes.map(n => `<tr>
    <td data-label="" class="mono muted" style="width:120px">${brisbane(n.tsUtc)}</td>
    <td data-label="" style="width:120px"><span class="tag" style="color:${col(n.severity)}">${esc(n.kind)}</span></td>
    <td data-label="">${esc(n.message || (n.fromState ? n.fromState + ' → ' + n.toState : ''))}${n.recordingId ? ` <span class="muted">#${n.recordingId}</span>` : ''}</td></tr>`).join('')}</tbody></table>`;
}
function upcomingEvents(events, leagues) {
  return `<div class="dash-rec">${events.map(e => {
    const c = leagueColor(leagues.find(l => l.id === e.leagueId) || e);
    return `<div class="prow clickrow" onclick="openCalEvent(${e.id})">
      <span class="lg-dot" style="background:${c}"></span>
      <div class="prow-main"><b>${esc(e.title)}</b><div class="prow-sub">${brisbane(e.start)} · ${esc(e.league)}</div></div>
      <div class="prow-side">${e.monitored ? '<span class="tag ok">monitored</span>' : ''}</div>
    </div>`;
  }).join('')}</div>`;
}
function leagueChips(leagues) {
  return `<div class="league-chips">${leagues.map(l => `
    <div class="lchip" onclick="location.hash='#/calendar?league=${l.id}'" title="${esc(l.name)}">
      ${l.poster ? `<img src="${esc(l.poster)}" alt="" loading="lazy"/>` : `<span class="lchip-dot" style="background:${leagueColor(l)}"></span>`}
      <div class="lchip-meta"><b>${esc(l.name)}</b><small>${esc(l.sport)} · ${l.events} event${l.events === 1 ? '' : 's'}</small></div>
    </div>`).join('')}</div>`;
}

// ---- Channels ----
PAGES.channels = {
  title: 'Channels',
  async render(el) {
    const sources = await api.get('/api/sources');
    el.innerHTML = `<div class="note">Channels are per source. Filter by source and by the provider's <b>group</b> (category); the group list updates to the selected source. Channels & groups appear after a source <b>ingest</b> (Sources page).</div>
      <div class="toolbar" style="margin-top:16px">
        <select id="chSrc"><option value="all">All sources</option>${sources.map(s => `<option value="${s.id}">${esc(s.label)}</option>`).join('')}</select>
        <input id="chGrpQ" placeholder="filter groups (e.g. uk sports)…" style="max-width:200px"/>
        <select id="chGrp"><option value="all">All groups</option></select>
        <input id="chQ" class="grow" placeholder="search channels (keyword)…" />
      </div><div id="chWrap"></div>`;
    let allGroups = [];
    const renderGroups = () => {
      const q = $('#chGrpQ').value;
      const f = q ? allGroups.filter(g => tokensMatch(g, q)) : allGroups;
      $('#chGrp').innerHTML = `<option value="all">All groups${f.length ? ` (${f.length})` : ''}</option>` + f.slice(0, 800).map(g => `<option value="${esc(g)}">${esc(g)}</option>`).join('');
    };
    const loadGroups = async () => { allGroups = await api.get(`/api/channels/groups?source=${$('#chSrc').value}`); renderGroups(); };
    const draw = async () => {
      const rows = await api.get(`/api/channels?source=${$('#chSrc').value}&group=${encodeURIComponent($('#chGrp').value)}&q=${encodeURIComponent($('#chQ').value)}&take=500`);
      window._chanRows = {}; rows.forEach(c => window._chanRows[c.id] = c);
      $('#chWrap').innerHTML = rows.length ? `<table class="rtable"><thead><tr><th>Name</th><th>Group</th><th>Source</th><th>Quality</th><th></th></tr></thead><tbody>${rows.map(c => `
        <tr><td data-label="">${esc(c.name)}</td><td data-label="Group" class="muted">${esc(c.group || '')}</td><td data-label="Source" class="muted">${esc(c.sourceLabel)}</td><td data-label="Quality" class="mono muted">${esc(c.quality || '')}</td>
        <td data-label="" class="row acts" style="gap:6px;flex-wrap:nowrap">
          <button class="play-btn sm" title="Watch live preview" onclick="openPreview(${c.id},'${jsq(c.name)}')">${I.play} Watch</button>
          <button class="ghost sm" onclick="scheduleFor(${c.id})">${I.plus} Schedule</button>
          ${kebab([
            { label: `${I.play} Watch live preview`, fn: `openPreview(${c.id},'${jsq(c.name)}')` },
            { label: 'Schedule recording', fn: `scheduleFor(${c.id})` },
          ])}
        </td></tr>`).join('')}</tbody></table>`
        : emptyBox('No channels for this view. Run an ingest on the Sources page.');
    };
    $('#chSrc').addEventListener('change', async () => { await loadGroups(); $('#chGrp').value = 'all'; await draw(); });
    let gt; $('#chGrpQ').addEventListener('input', () => { clearTimeout(gt); gt = setTimeout(renderGroups, 150); });
    $('#chGrp').addEventListener('change', draw);
    let t; $('#chQ').addEventListener('input', () => { clearTimeout(t); t = setTimeout(draw, 250); });
    await loadGroups();
    await draw();
  },
};

// ---- Guide (timeline EPG) ----
const GUIDE_PX_PER_HOUR = 200;
// Channel-name column: narrower on phones so the timeline isn't pushed off-screen. The CSS (.g-corner/.g-ch) reads
// --gch, which renderGuide sets inline on .guide-inner from this same value, so the now-line offset stays aligned.
const guideChCol = () => (window.innerWidth <= 640 ? 78 : 250);
// Phone: tapping a channel NAME reveals the full name (heavily truncated at 78px) instead of starting a preview —
// the desktop click (and the row's play affordance there) still previews.
function chanTap(id, name) { if (window.innerWidth <= 640) return toast(name); openPreview(id, name); }
PAGES.guide = {
  title: 'Guide',
  async render(el) {
    const sources = await api.get('/api/sources');
    if (!sources.length) { el.innerHTML = emptyBox('Add a source first, then ingest channels and sync EPG.'); return; }
    // Default to the source that actually has EPG (most programmes), so the guide opens on data — not an empty source.
    const epgSrc = sources.filter(s => s.enabled && s.programmes > 0).sort((a, b) => b.programmes - a.programmes)[0];
    const defSrc = epgSrc || sources.find(s => s.enabled) || sources[0];
    const state = { sourceId: String(defSrc.id), group: 'all', q: '', start: Math.floor(Date.now() / 1000) - 1800, hours: 24, groups: [] };
    el.innerHTML = `
      <div class="toolbar">
        <select id="gSrc">${sources.map(s => `<option value="${s.id}" ${String(s.id) === state.sourceId ? 'selected' : ''}>${esc(s.label)}${s.enabled ? '' : ' — disabled'}</option>`).join('')}</select>
        <input id="gGrpQ" placeholder="filter groups…" style="max-width:180px"/>
        <select id="gGrp"><option value="all">All groups</option></select>
        <input id="gQ" placeholder="search channels…" style="max-width:200px"/>
        <span class="grow"></span>
        <button class="ghost sm" id="gPrev">‹ earlier</button>
        <button class="ghost sm" id="gNow">now</button>
        <button class="ghost sm" id="gNext">later ›</button>
        <select id="gHours"><option value="6">6h</option><option value="12">12h</option><option value="24" selected>24h</option></select>
      </div>
      <div class="guide-legend"><span class="lg lg-live">live now</span><span class="lg lg-rec">recording</span><span class="lg lg-pad">pre/post buffer</span><span class="lg lg-play">▶ click a channel name to watch · click a programme to schedule</span></div>
      <div id="gWrap" class="loading">…</div>`;

    const renderGroups = () => {
      const q = $('#gGrpQ').value;
      const f = q ? state.groups.filter(g => tokensMatch(g, q)) : state.groups;
      $('#gGrp').innerHTML = `<option value="all">All groups${f.length ? ` (${f.length})` : ''}</option>` + f.slice(0, 800).map(g => `<option value="${esc(g)}" ${g === state.group ? 'selected' : ''}>${esc(g)}</option>`).join('');
    };
    const loadGroups = async () => { state.groups = await api.get(`/api/channels/groups?source=${state.sourceId}`); renderGroups(); };

    const draw = async () => {
      $('#gWrap').innerHTML = '<div class="loading">Loading guide…</div>';
      const g = await api.get(`/api/guide?source=${state.sourceId}&group=${encodeURIComponent(state.group)}&q=${encodeURIComponent(state.q)}&start=${state.start}&hours=${state.hours}`);
      renderGuide($('#gWrap'), g);
    };

    $('#gSrc').addEventListener('change', async () => { state.sourceId = $('#gSrc').value; state.group = 'all'; await loadGroups(); await draw(); });
    let gt; $('#gGrpQ').addEventListener('input', () => { clearTimeout(gt); gt = setTimeout(renderGroups, 150); });
    $('#gGrp').addEventListener('change', () => { state.group = $('#gGrp').value; draw(); });
    let qt; $('#gQ').addEventListener('input', () => { clearTimeout(qt); qt = setTimeout(() => { state.q = $('#gQ').value; draw(); }, 300); });
    $('#gHours').addEventListener('change', () => { state.hours = parseInt($('#gHours').value); draw(); });
    $('#gPrev').addEventListener('click', () => { state.start -= state.hours * 3600; draw(); });
    $('#gNext').addEventListener('click', () => { state.start += state.hours * 3600; draw(); });
    $('#gNow').addEventListener('click', () => { state.start = Math.floor(Date.now() / 1000) - 1800; draw(); });

    await loadGroups();
    await draw();
  },
};

function renderGuide(wrap, g) {
  if (!g.channels || !g.channels.length) { wrap.innerHTML = emptyBox('No channels/EPG for this view. Ingest channels and sync this source’s EPG (Sources page).'); return; }
  const chCol = guideChCol();
  window._guideChans = {}; g.channels.forEach(c => window._guideChans[c.channelId] = c);
  const winStart = g.windowStart, winEnd = g.windowEnd, now = g.now;
  const totalH = (winEnd - winStart) / 3600;
  const trackW = totalH * GUIDE_PX_PER_HOUR;
  const xOf = t => Math.max(0, Math.min(trackW, (t - winStart) / 3600 * GUIDE_PX_PER_HOUR));

  // hour ticks
  let ticks = '';
  const firstTick = Math.ceil(winStart / 3600) * 3600;
  for (let t = firstTick; t <= winEnd; t += 3600)
    ticks += `<div class="g-tick" style="left:${xOf(t)}px">${hhmm(t)}</div>`;

  const rows = g.channels.map(c => {
    const recs = c.recordings || [];
    const overCore = (s, e) => recs.some(r => r.coreStart < e && r.coreEnd > s);    // the actual recording → red
    const overPad = (s, e) => recs.some(r => r.start < e && r.end > s);              // pre/post buffer → orange
    const blocks = (c.programmes || []).filter(p => p.start && p.stop > winStart && p.start < winEnd).map(p => {
      const left = xOf(p.start), w = Math.max(2, xOf(p.stop) - left);
      const live = p.start <= now && now < p.stop;
      const core = overCore(p.start, p.stop);
      const pad = !core && overPad(p.start, p.stop);
      const cls = 'g-prog' + (live ? ' live' : '') + (core ? ' rec' : '') + (pad ? ' pad' : '');
      return `<div class="${cls}" style="left:${left}px;width:${w}px" data-ch="${c.channelId}" data-start="${p.start}" data-stop="${p.stop}" data-title="${esc(p.Title || p.title || '')}" title="${esc(p.Title || p.title || '')} · ${hhmm(p.start)}-${hhmm(p.stop)}"><span class="g-pt">${hhmm(p.start)}</span> ${esc(p.Title || p.title || '')}</div>`;
    }).join('');
    const body = blocks || `<div class="g-empty">no guide data</div>`;
    return `<div class="g-row"><div class="g-ch" title="Watch ${esc(c.name)}" onclick="openPreview(${c.channelId},'${jsq(c.name)}')">${I.play}<span>${esc(c.name)}</span></div><div class="g-track" style="width:${trackW}px">${body}</div></div>`;
  }).join('');

  wrap.innerHTML = `<div class="guide-scroll"><div class="guide-inner" style="--gch:${chCol}px;width:${chCol + trackW}px">
    <div class="g-head"><div class="g-corner">${new Date(winStart * 1000).toLocaleDateString('en-AU', { timeZone: 'Australia/Brisbane', weekday: 'short', day: '2-digit', month: 'short' })}</div><div class="g-hours" style="width:${trackW}px">${ticks}</div></div>
    ${rows}
    ${(now >= winStart && now <= winEnd) ? `<div class="g-now" style="left:${chCol + xOf(now)}px"></div>` : ''}
  </div></div>`;

  wrap.querySelectorAll('.g-prog').forEach(b => b.addEventListener('click', () => {
    scheduleFromGuide(parseInt(b.dataset.ch), parseInt(b.dataset.start), parseInt(b.dataset.stop), b.dataset.title);
  }));
}

function scheduleFromGuide(channelId, start, stop, title) {
  const c = (window._guideChans || {})[channelId] || {};
  openScheduleModal({ sourceId: c.SourceId || c.sourceId, group: c.group, channelId, channelName: c.name, title, startLocal: toLocalInput(start), durationMin: Math.max(1, Math.round((stop - start) / 60)) });
}

// ---- Sources ----
PAGES.sources = {
  title: 'Sources',
  actions: () => `<button onclick="openSourceModal(null)">${I.plus} Add source</button><button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  menuActions: [{ label: 'Refresh', fn: 'render()' }],
  async render(el) {
    const s = await api.get('/api/sources');
    window._sources = s;
    el.innerHTML = `<div class="note warn">⚠ Your provider allows <b>one stream per login</b>. <b>Ingest</b>, <b>Sync EPG</b> and <b>Live preview</b> use that login's single slot — don't run them while recording on the same source.</div>
      <div class="section">${s.length ? `<table class="rtable"><thead><tr><th>Label</th><th>Type</th><th>Host</th><th>User</th><th>Slot</th><th>Channels</th><th>EPG</th><th></th></tr></thead><tbody>${s.map(x => `
        <tr><td data-label=""><b>${esc(x.label)}</b></td><td data-label="Type" class="muted">${esc(x.type)}</td><td data-label="Host" class="mono muted">${esc(x.host)}${x.port ? ':' + x.port : ''}</td><td data-label="User" class="mono">${esc(x.username)}</td>
        <td data-label="Slot"><span class="tag ${x.slotFree ? 'ok' : 'busy'}">${x.slotFree ? 'free' : 'busy'}</span></td>
        <td data-label="Channels" class="mono">${x.channels}</td>
        <td data-label="EPG" class="mono muted">${x.programmes || 0}${x.epgOverride ? ' <span class="tag" title="external EPG override active">ext</span>' : ''}</td>
        <td data-label="" class="row acts" style="gap:6px;flex-wrap:wrap">
          <button class="ghost sm" onclick="ingest(${x.id},'${jsq(x.label)}')">Ingest</button>
          <button class="ghost sm" onclick="syncEpg(${x.id},'${jsq(x.label)}')">EPG</button>
          <button class="ghost sm" onclick="openSourceModal(${x.id})">Edit</button>
          <button class="danger sm" onclick="deleteSource(${x.id},'${jsq(x.label)}')">Del</button>
          ${kebab([
            { label: 'Ingest channels', fn: `ingest(${x.id},'${jsq(x.label)}')` },
            { label: 'Sync EPG', fn: `syncEpg(${x.id},'${jsq(x.label)}')` },
            { label: 'Edit source', fn: `openSourceModal(${x.id})` },
            { label: 'Delete source', fn: `deleteSource(${x.id},'${jsq(x.label)}')`, danger: true },
          ])}
        </td></tr>`).join('')}</tbody></table>`
        : emptyBox('No sources configured. Click “Add source” to set one up.')}</div>`;
  },
};

// ---- Leagues + mappings ----
PAGES.leagues = {
  title: 'Leagues',
  actions: () => `<button onclick="openLeagueModal(null)">${I.plus} Add league</button><button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  menuActions: [{ label: 'Refresh', fn: 'render()' }],
  async render(el) {
    const ls = await api.get('/api/leagues');
    window._leagues = ls;
    el.innerHTML = `<div class="page-wide"><div class="note">A <b>monitored</b> league auto-records its events on its mapped channel(s). Leagues come from <b>TheSportsDB</b> — pick a sport then search the league; posters &amp; events sync automatically.</div>
      <div class="section"><h2>Leagues ${ls.length ? `<span class="count-pill">${ls.length}</span>` : ''}</h2>${ls.length ? `<table class="rtable"><thead><tr><th></th><th>League</th><th>Sport</th><th>Events</th><th>Maps</th><th>Monitored</th><th></th></tr></thead><tbody>${ls.map(l => `
        <tr><td class="hide-sm">${l.poster ? `<img class="lg-poster" src="${esc(l.poster)}" alt=""/>` : ''}</td>
        <td data-label=""><span class="lg-dot" style="background:${leagueColor(l)}" title="calendar colour"></span><b>${esc(l.name)}</b>${l.externalLeagueId ? ` <span class="tag" title="TheSportsDB id">#${esc(l.externalLeagueId)}</span>` : ''}${l.monitoredTeams && l.monitoredTeams.length ? `<div class="muted" style="font-size:11px">following ${l.monitoredTeams.length} team${l.monitoredTeams.length === 1 ? '' : 's'}: ${esc(l.monitoredTeams.map(t => t.name).filter(Boolean).join(', '))}</div>` : ''}${l.monitoredSessions && l.monitoredSessions.length ? `<div class="muted" style="font-size:11px">sessions: ${esc(l.monitoredSessions.join(', '))}</div>` : ''}${l.autoStopMode === 'fixed' ? `<div class="muted" style="font-size:11px">auto-stop: fixed</div>` : ''}</td><td data-label="Sport" class="muted">${esc(l.sport)}</td>
        <td data-label="Events" class="mono">${l.events}</td><td data-label="Maps" class="mono">${l.mappings}</td>
        <td data-label="Monitored"><span class="tag ${l.monitored ? 'ok' : ''}">${l.monitored ? 'yes' : 'no'}</span></td>
        <td data-label="" class="row acts" style="gap:6px;flex-wrap:nowrap">
          <button class="ghost sm" onclick="openMapModal(${l.id},'${jsq(l.name)}')">Map</button>
          <button class="ghost sm" onclick="syncLeague(${l.id})">Sync</button>
          <button class="ghost sm" onclick="reresolveLeague(${l.id})" title="Update this league's scheduled recordings to its current channel mapping">Re-resolve</button>
          <button class="ghost sm" onclick="location.hash='#/calendar?league=${l.id}'">Events</button>
          <button class="ghost sm" onclick="openLeagueModal(${l.id})">Edit</button>
          <button class="danger sm" onclick="deleteLeague(${l.id},'${jsq(l.name)}')">Del</button>
          ${kebab([
            { label: 'Map channel', fn: `openMapModal(${l.id},'${jsq(l.name)}')` },
            { label: 'Sync events', fn: `syncLeague(${l.id})` },
            { label: 'Re-resolve recordings', fn: `reresolveLeague(${l.id})`, title: "Update this league's scheduled recordings to its current channel mapping" },
            { label: 'View events', fn: `location.hash='#/calendar?league=${l.id}'` },
            { label: 'Edit league', fn: `openLeagueModal(${l.id})` },
            { label: 'Delete league', fn: `deleteLeague(${l.id},'${jsq(l.name)}')`, danger: true },
          ])}
        </td></tr>`).join('')}</tbody></table>` : emptyBox('No leagues yet. Add one and pin a channel to start auto-recording.')}</div>
      <div class="section"><h2>Channel mappings</h2>
        <div class="note">
          <b>How mappings &amp; ranks work.</b> Each row maps a league to a channel. <b>Rank</b> is the order DVarr tries them — lowest first: <b>rank&nbsp;1</b> is the primary (first choice), rank&nbsp;2, 3… are fallbacks. When an event is due, DVarr records the best-ranked channel; if that channel <b>won't open or drops out</b>, it automatically walks down the list to the next working channel. Add a few channels that all carry the same event so there's always a backup.
          <div style="margin-top:6px"><b>★ Pinned</b> means your pick wins over EPG guesswork — a similar-looking guide entry can't hijack the recording. Unpinned mappings still work but let a strong EPG title match reorder them.</div>
          <div class="muted" style="margin-top:6px;font-size:12px;line-height:1.5">All fallbacks must be on the <b>same provider login</b> as the primary (one stream per login). With <b>content check</b> on (Settings), DVarr also fails over when a channel is alive but stuck on a dead <b>black or frozen</b> slate. It does <b>not</b> watch the picture to decide whether the “right” match is on — that relies on your ranks, the EPG, and pre/post-padding, which is exactly why a pre-show/intro is captured rather than skipped.</div>
        </div>
        <div class="toolbar" style="margin:12px 0">
          <select id="mapLeagueFilter"><option value="">All leagues</option>${ls.map(l => `<option value="${l.id}">${esc(l.name)}</option>`).join('')}</select>
          <span class="muted" id="mapCount"></span>
        </div>
        <div id="mapsWrap" class="loading">…</div></div></div>`;
    const maps0 = await api.get('/api/mappings');
    const maps = Array.isArray(maps0) ? maps0 : [];
    const drawMaps = () => {
      const lf = $('#mapLeagueFilter').value;
      const rows = lf ? maps.filter(m => String(m.leagueId) === lf) : maps;
      $('#mapCount').textContent = maps.length ? (lf ? `${rows.length} of ${maps.length} mapping${maps.length === 1 ? '' : 's'}` : `${maps.length} mapping${maps.length === 1 ? '' : 's'}`) : '';
      $('#mapsWrap').innerHTML = rows.length ? `<table class="rtable"><thead><tr><th>League</th><th>Channel</th><th>Source</th><th>Rank</th><th>Pinned</th><th></th></tr></thead><tbody>${rows.map(m => {
        const lg = ls.find(x => x.id === m.leagueId);
        return `<tr><td data-label="">${esc(lg ? lg.name : '#' + m.leagueId)}</td><td data-label="Channel">${esc(m.channel)}</td><td data-label="Source" class="muted">${esc(m.source)}</td><td data-label="Rank" class="mono">${m.rank}</td><td data-label="Pinned">${m.pinned ? '★' : ''}</td><td data-label="" class="acts"><button class="danger sm" onclick="deleteMapping(${m.id})">Del</button>${kebab([{ label: 'Remove mapping', fn: `deleteMapping(${m.id})`, danger: true }])}</td></tr>`;
      }).join('')}</tbody></table>` : emptyBox(lf ? 'No mappings for this league yet — use “Map” on it above.' : 'No mappings. Use “Map” on a league to pin a channel.');
    };
    $('#mapLeagueFilter').addEventListener('change', drawMaps);
    drawMaps();
  },
};

// ---- Calendar (monthly grid; events per day, colour-coded by league) ----
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const DOW = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
PAGES.calendar = {
  title: 'Calendar',
  actions: () => `<button onclick="openEventModal()">${I.plus} Add event</button><button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  menuActions: [{ label: 'Refresh', fn: 'render()' }],
  async render(el) {
    const params = new URLSearchParams((location.hash.split('?')[1]) || '');
    const leagueFilter = params.get('league') || '';
    const ls = await api.get('/api/leagues'); window._leagues = ls;
    window._calEvents = {};
    const today = bneParts(Math.floor(Date.now() / 1000));
    // selDay drives the phone-only "selected day" list under the mini-grid (default = today); harmless on desktop.
    const state = { y: today.y, m: today.m, league: leagueFilter, selDay: bneCellKey(today.y, today.m, today.d) };
    let byDay = {}; // events bucketed by Brisbane day — shared by the grid and the phone day list

    el.innerHTML = `
      <div class="toolbar cal-toolbar">
        <button class="ghost sm" id="calPrev">‹</button>
        <div id="calTitle" class="cal-title"></div>
        <button class="ghost sm" id="calNext">›</button>
        <button class="ghost sm" id="calToday">Today</button>
        <button class="ghost sm" id="calSubscribe" title="Subscribe to this calendar in Google/Apple Calendar">${I.calendar} Subscribe</button>
        <span class="grow"></span>
        <select id="calLeague"><option value="">All leagues</option>${ls.map(l => `<option value="${l.id}" ${String(l.id) === leagueFilter ? 'selected' : ''}>${esc(l.name)}</option>`).join('')}</select>
      </div>
      <div id="calGrid" class="loading">…</div>
      <div id="calDayList" class="cal-daylist"></div>`;

    // Phone (≤640): the month grid shows colour DOTS; tapping a day lists its events as cards below the grid.
    // Desktop keeps the full event cards and this list stays empty + display:none.
    const phoneCal = () => window.matchMedia('(max-width:640px)').matches;
    const renderDayList = () => {
      const host = $('#calDayList'); if (!host) return;
      if (!phoneCal()) { host.innerHTML = ''; return; }
      const evs = byDay[state.selDay] || [];
      const label = new Date((state.selDay * 86400 - BNE_OFFSET) * 1000).toLocaleDateString('en-AU', { timeZone: 'Australia/Brisbane', weekday: 'long', day: 'numeric', month: 'long' });
      host.innerHTML = `<div class="section"><h2>${esc(label)}</h2>${evs.length ? `<div class="panel"><div class="panel-body">${evs.map(e => {
        const c = leagueColor(ls.find(l => l.id === e.leagueId) || e);
        return `<div class="prow clickrow" onclick="openCalEvent(${e.id})"><span class="lg-dot" style="background:${c}"></span><div class="prow-main"><b>${esc(e.title)}</b><div class="prow-sub">${hhmm(e.start)} · ${esc(e.league)}</div></div><div class="prow-side">${e.monitored ? '<span class="tag ok">monitored</span>' : ''}</div></div>`;
      }).join('')}</div></div>` : `<div class="empty" style="padding:22px">No events this day.</div>`}</div>`;
    };
    $('#calGrid').addEventListener('click', e => {
      if (!phoneCal()) return; // desktop: event cards keep their own click handlers
      const cell = e.target.closest('.cal-cell');
      if (!cell || !cell.dataset.day) return;
      state.selDay = parseInt(cell.dataset.day);
      $('#calGrid').querySelectorAll('.cal-cell.sel').forEach(x => x.classList.remove('sel'));
      cell.classList.add('sel');
      renderDayList();
    });

    const draw = async () => {
      $('#calTitle').textContent = `${MONTHS[state.m]} ${state.y}`;
      const from = bneMonthStart(state.y, state.m);
      const to = bneMonthStart(state.m === 11 ? state.y + 1 : state.y, (state.m + 1) % 12) - 1;
      const events = await api.get(`/api/events?from=${from}&to=${to}${state.league ? `&leagueId=${state.league}` : ''}`);
      window._calEvents = {}; events.forEach(e => { window._calEvents[e.id] = e; });

      // bucket events by Brisbane day
      byDay = {};
      events.forEach(e => { (byDay[bneDayKey(e.start)] ||= []).push(e); });
      Object.values(byDay).forEach(list => list.sort((a, b) => a.start - b.start));

      // build the month grid (weeks start Monday)
      const firstDow = (new Date(Date.UTC(state.y, state.m, 1)).getUTCDay() + 6) % 7; // 0=Mon
      const daysInMonth = new Date(Date.UTC(state.y, state.m + 1, 0)).getUTCDate();
      const todayKey = bneCellKey(today.y, today.m, today.d);
      // Keep the phone day-list selection inside the displayed month (today when visible, else the 1st).
      const monthFirstKey = bneCellKey(state.y, state.m, 1), monthLastKey = bneCellKey(state.y, state.m, daysInMonth);
      if (state.selDay < monthFirstKey || state.selDay > monthLastKey)
        state.selDay = (todayKey >= monthFirstKey && todayKey <= monthLastKey) ? todayKey : monthFirstKey;

      let cells = '';
      for (let i = 0; i < firstDow; i++) cells += `<div class="cal-cell empty-cell"></div>`;
      for (let d = 1; d <= daysInMonth; d++) {
        const key = bneCellKey(state.y, state.m, d);
        const evs = byDay[key] || [];
        const isToday = key === todayKey;
        cells += `<div class="cal-cell${isToday ? ' today' : ''}${key === state.selDay ? ' sel' : ''}" data-day="${key}">
          <div class="cal-daynum">${d}</div>
          <div class="cal-events">${evs.map(e => {
          const bg = leagueColor(ls.find(l => l.id === e.leagueId) || e);
          return `<div class="cal-ev" style="background:${bg};color:${textOn(bg)}" title="${esc(e.title)} · ${esc(e.league)} · ${brisbane(e.start)}" onclick="openCalEvent(${e.id})"><span class="cal-ev-t">${hhmm(e.start)}</span> ${esc(e.title)}</div>`;
        }).join('')}</div></div>`;
      }
      $('#calGrid').innerHTML = `<div class="cal-grid"><div class="cal-head">${DOW.map(d => `<div>${d}</div>`).join('')}</div><div class="cal-body">${cells}</div></div>`;
      renderDayList();
    };

    $('#calPrev').addEventListener('click', () => { if (state.m === 0) { state.m = 11; state.y--; } else state.m--; draw(); });
    $('#calNext').addEventListener('click', () => { if (state.m === 11) { state.m = 0; state.y++; } else state.m++; draw(); });
    $('#calToday').addEventListener('click', () => { const t = bneParts(Math.floor(Date.now() / 1000)); state.y = t.y; state.m = t.m; draw(); });
    $('#calSubscribe').addEventListener('click', openCalendarFeedModal);
    $('#calLeague').addEventListener('change', () => { state.league = $('#calLeague').value; draw(); });
    await draw();
  },
};
// Click an event card → details + monitor/resolve.
function openCalEvent(id) {
  const e = (window._calEvents || {})[id]; if (!e) return;
  const dur = e.end ? Math.max(1, Math.round((e.end - e.start) / 60)) : 180;
  modal(`<h2>${esc(e.title)}</h2><div class="fields">
    <div class="muted">${esc(e.league)} · ${esc(e.sport || '')}</div>
    <div>${brisbane(e.start)} (Brisbane)${e.dateOnly ? ' <span class="tag">date-only</span>' : ''} · <span class="tag">${esc(e.status)}</span></div>
    <div class="muted" style="font-size:12px">A <b>monitored</b> event auto-records on the league's pinned channel. “Resolve” previews which channel it picks.</div>
    </div><div class="foot">
      <button class="ghost" onclick="closeModals()">Close</button>
      <button class="ghost" onclick="resolvePreview(${e.id})">Resolve</button>
      <button onclick="monitorEvent(${e.id},${!e.monitored})">${e.monitored ? 'Unmonitor' : 'Monitor (auto-record)'}</button>
    </div>`, 'min(480px,94vw)');
}

// Subscribe (ICS feed): copy-me local + public URLs for Google/Apple Calendar. The feed path (with its private token)
// comes from GET /api/calendar/url; the public URL prepends the `public_base_url` setting (prompted for if unset).
async function openCalendarFeedModal() {
  const [feed, settings] = await Promise.all([api.get('/api/calendar/url'), api.get('/api/settings')]);
  const path = (feed && feed.url) || '';
  const publicBase = ((settings && settings.public_base_url) || '').replace(/\/+$/, '');
  if (!path) { toast('Could not load the calendar link', 'err'); return; }
  modal(calendarFeedBody(path, publicBase), 'min(560px,94vw)');
}
// The modal's inner HTML — re-rendered in place after the public URL is saved so no reopen is needed.
function calendarFeedBody(path, publicBase) {
  const local = location.origin + path;
  const pub = publicBase ? publicBase + path : '';
  const publicRow = pub
    ? `<label class="field">Public address <span class="muted">(from any network)</span>
        <div class="copy-row"><input id="feedPublic" readonly value="${esc(pub)}"/><button class="ghost sm" onclick="copyText($('#feedPublic').value,'#feedPublic')">Copy</button></div>
        <button class="ghost sm" style="align-self:flex-start;margin-top:6px" onclick="editCalendarPublicBase('${jsq(path)}')">change public address</button></label>`
    : `<label class="field">Public address <span class="muted">(optional — for subscribing away from home)</span>
        <div class="muted" style="font-size:12px;margin-bottom:4px">Set your DVarr public URL to build a link that works off your home network.</div>
        <div class="copy-row"><input id="feedBase" placeholder="https://dvr.example.com"/><button class="ghost sm" onclick="saveCalendarPublicBase('${jsq(path)}')">Save</button></div></label>`;
  return `<h2>Calendar feed (ICS)</h2>
    <div class="fields">
      <div class="muted" style="font-size:12.5px;line-height:1.6">Subscribe to this feed in <b>Google Calendar</b> (Other calendars → From URL) or <b>Apple Calendar</b> to keep every followed event on your phone/PC calendar, refreshed automatically. The link contains a private token — treat it like a password.</div>
      <label class="field">This address <span class="muted">(on this network)</span>
        <div class="copy-row"><input id="feedLocal" readonly value="${esc(local)}"/><button class="ghost sm" onclick="copyText($('#feedLocal').value,'#feedLocal')">Copy</button></div></label>
      ${publicRow}
    </div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Close</button></div>`;
}
// Validate + persist public_base_url (empty or http(s)://…, trailing slash stripped), then re-render the modal body.
async function saveCalendarPublicBase(path) {
  const raw = ($('#feedBase')?.value || '').trim().replace(/\/+$/, '');
  if (raw && !/^https?:\/\//i.test(raw)) return toast('Public address must start with http:// or https://', 'err');
  const r = await api.put('/api/settings', { public_base_url: raw });
  if (r.error) return toast(r.error, 'err');
  toast('Public address saved', 'ok');
  const bg = $('.modal-bg'); const box = bg && bg.querySelector('.modal');
  if (box) box.innerHTML = calendarFeedBody(path, raw) + (box.querySelector('.modal-x')?.outerHTML || '');
}
// "change" affordance: blank the setting, re-render the modal back to the input row (user can re-save a new URL).
async function editCalendarPublicBase(path) {
  const r = await api.put('/api/settings', { public_base_url: '' });
  if (r.error) return toast(r.error, 'err');
  const bg = $('.modal-bg'); const box = bg && bg.querySelector('.modal');
  if (box) box.innerHTML = calendarFeedBody(path, '') + (box.querySelector('.modal-x')?.outerHTML || '');
}

// ---- Activity ----
PAGES.activity = {
  title: 'Activity',
  actions: () => `<button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  menuActions: [{ label: 'Refresh', fn: 'render()' }], // phone topbar ⋯ (mirrors the ghost button above)
  async render(el) {
    const [notes, ticks] = await Promise.all([api.get('/api/notifications?take=60'), api.get('/api/ticks?take=15')]);
    el.innerHTML = `
      <div class="section"><h2>Notifications</h2>${notes.length ? notesList(notes) : emptyBox('No notifications yet.')}</div>
      <div class="section"><h2>Scheduler ticks</h2>${ticks.length ? `<table class="rtable"><thead><tr><th>Time</th><th>Examined</th><th>Started</th><th>Missed</th><th>Conflicts</th><th>ms</th></tr></thead><tbody>${ticks.map(t => `
        <tr><td data-label="" class="mono muted">${brisbane(t.tickUtc)}</td><td data-label="Examined" class="mono">${t.recordingsExamined}</td><td data-label="Started" class="mono">${t.started}</td><td data-label="Missed" class="mono">${t.missed}</td><td data-label="Conflicts" class="mono">${t.conflicts}</td><td data-label="Duration (ms)" class="mono muted">${t.durationMs}</td></tr>`).join('')}</tbody></table>`
        : emptyBox('Scheduler has not ticked yet.')}</div>`;
  },
};

// ---- Conflicts (credit-aware planning) ----
PAGES.conflicts = {
  title: 'Conflicts',
  actions: () => `<button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  menuActions: [{ label: 'Refresh', fn: 'render()' }], // phone topbar ⋯ (mirrors the ghost button above)
  async render(el) {
    const [data, sources] = await Promise.all([api.get('/api/conflicts'), api.get('/api/sources')]);
    const creds = data.credentials || [], conflicts = data.conflicts || [];
    const enabled = (sources || []).filter(s => s.enabled);

    const conflictCards = conflicts.length ? conflicts.map(r => `
      <div class="card" style="border-color:var(--warn);margin-bottom:12px">
        <div style="min-width:0"><b>${esc(r.title || ('Recording #' + r.id))}</b>
          <div class="muted" style="font-size:12px">${brisbane(r.startUtc)} – ${hhmm(r.endUtc)} · ${esc(r.reason || 'conflict')}</div></div>
        <div class="row acts-row" style="margin-top:10px;gap:8px;flex-wrap:wrap">
          ${enabled.map(s => `<button class="sm ghost" onclick="reassignRec(${r.id},${s.id})">Record on ${esc(s.label)}</button>`).join('')}
          <button class="sm" onclick="bumpRec(${r.id})">Bump to Can't-Miss</button>
          ${kebab([
            ...enabled.map(s => ({ label: `Record on ${esc(s.label)}`, fn: `reassignRec(${r.id},${s.id})` })),
            { label: "Bump to Can't-Miss", fn: `bumpRec(${r.id})` },
          ])}
        </div>
      </div>`).join('') : emptyBox('No conflicts — every monitored event has a free login.');

    const credCards = creds.map(c => `
      <div class="card">
        <h3>${esc(c.label)} — ${c.load.length} scheduled (next 14 days)</h3>
        ${c.load.length ? c.load.map(r => `
          <div class="row" style="justify-content:space-between;gap:10px;border-bottom:1px solid var(--line);padding:7px 0">
            <div style="min-width:0">
              <div style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${esc(r.title || ('Recording #' + r.id))}</div>
              <div class="muted" style="font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${esc(r.channel || '')} · ${brisbane(r.startUtc)}–${hhmm(r.endUtc)}</div>
            </div>
            <span class="pill ${sc(r.state)}">${esc(r.state)}</span>
          </div>`).join('') : `<div class="muted" style="font-size:12px">Idle.</div>`}
      </div>`).join('') || emptyBox('No enabled sources yet.');

    el.innerHTML = `
      <div class="section"><h2>Conflicts</h2>${conflictCards}</div>
      <div class="section"><h2>Load by login (1 stream each)</h2>
        <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:16px">${credCards}</div></div>`;
  },
};
window.reassignRec = async (id, sourceId) => { const r = await api.post(`/api/recordings/${id}/reassign`, { sourceId }); if (r.error) toast(r.error, 'err'); else { toast('Reassigned', 'ok'); render(); } };
window.bumpRec = async (id) => { const r = await api.post(`/api/recordings/${id}/reassign`, { priority: 'cant_miss' }); if (r.error) toast(r.error, 'err'); else { toast('Priority raised to Can\'t-Miss', 'ok'); render(); } };

// ---- Settings ----
// Per-setting metadata: clear title (t), one-sentence help (h), group (g), and input type (ty). Keys not listed here
// still render (under "Advanced") so a new backend setting is never hidden. The flat PUT payload is unchanged.
const SETTINGS_GROUPS = ['Recording', 'Reliability', 'Scheduling', 'Guide', 'TheSportsDB', 'Integrations', 'Display', 'Backups'];
// v1.20.0: the functional groups roll up into 5 top-level Settings tabs (laid out in columns/rows, not one long scroll).
const SETTINGS_TABS = ['Recording', 'Reliability', 'Scheduling & EPG', 'Data sources', 'Advanced'];
const GROUP_TAB = { Recording: 'Recording', Reliability: 'Reliability', Scheduling: 'Scheduling & EPG', Guide: 'Scheduling & EPG', TheSportsDB: 'Data sources', Integrations: 'Data sources', Display: 'Data sources', Backups: 'Data sources', Advanced: 'Advanced' };
const SETTINGS_META = {
  max_global_concurrent_recordings: { g: 'Recording', t: 'Max simultaneous recordings', h: 'The most recordings DVarr will run at once across all logins.', ty: 'int' },
  default_pre_pad_s: { g: 'Recording', t: 'Pre-roll padding (seconds)', h: 'How long before an event starts to begin recording.', ty: 'int' },
  default_post_pad_s: { g: 'Recording', t: 'Post-roll padding (seconds)', h: 'How long after an event ends to keep recording.', ty: 'int' },
  retry_at_event_start: { g: 'Recording', t: 'Retry at event start', h: 'If a recording captures nothing during pre-roll (the channel isn’t live yet), make one fresh attempt at the real start time.', ty: 'bool' },
  recorder_input_mode: { g: 'Recording', t: 'Recorder input mode', h: 'How the stream is ingested. Leave as “direct_ts”.', ty: 'text' },
  bitrate_floor_kbps_sd: { g: 'Reliability', t: 'SD bitrate floor (kbps)', h: 'Below this, an SD stream is treated as bad and fails over.', ty: 'int' },
  bitrate_floor_kbps_hd: { g: 'Reliability', t: 'HD bitrate floor (kbps)', h: 'Below this, an HD stream is treated as bad and fails over.', ty: 'int' },
  segment_no_progress_timeout_s: { g: 'Reliability', t: 'Stall timeout (seconds)', h: 'Seconds with no data written before the recorder relaunches / fails over.', ty: 'int' },
  content_verify_enabled: { g: 'Reliability', t: 'Dead-feed detection', h: 'Watch for a black / frozen / silent slate and fail over when the feed is dead. Opt-in.', ty: 'bool' },
  content_probe_interval_s: { g: 'Reliability', t: 'Dead-feed check interval (seconds)', h: 'How often to check for a dead feed when detection is on.', ty: 'int' },
  content_dead_timeout_s: { g: 'Reliability', t: 'Dead-feed timeout (seconds)', h: 'Seconds of dead feed before failing over.', ty: 'int' },
  content_verify_hwaccel: { g: 'Reliability', t: 'Dead-feed GPU decode', h: 'Decode the dead-feed check on the GPU (“cuda” = Nvidia NVDEC). Blank or “none” = CPU. Keeps detection near-free.', ty: 'text' },
  content_verify_fps: { g: 'Reliability', t: 'Dead-feed sample rate (fps)', h: 'Frames/second the black & freeze check samples — 1 is ample; 0 = every frame (much higher CPU/GPU).', ty: 'int' },
  finalize_deoverlap_enabled: { g: 'Reliability', t: 'De-overlap finished files', h: 'Trim duplicate seconds the provider re-serves on reconnect so playback never jumps backwards.', ty: 'bool' },
  clean_eof_instant_relaunch: { g: 'Reliability', t: 'Instant relaunch on clean drop', h: 'Ride momentary line drops by relaunching immediately on a clean stream close (no “Recovering” churn).', ty: 'bool' },
  tick_interval_s: { g: 'Scheduling', t: 'Scheduler tick (seconds)', h: 'How often DVarr checks for due recordings.', ty: 'int' },
  auto_schedule_interval_s: { g: 'Scheduling', t: 'Auto-schedule interval (seconds)', h: 'How often DVarr scans monitored events and plans recordings.', ty: 'int' },
  event_sync_interval_s: { g: 'Scheduling', t: 'Event sync interval (seconds)', h: 'How often the fixture list is refreshed from TheSportsDB.', ty: 'int' },
  default_event_duration_s: { g: 'Guide', t: 'Default event length (seconds)', h: 'Assumed length when the source gives no end time (7200 = 2 h). A per-league override always wins.', ty: 'int' },
  event_duration_overrides_json: { g: 'Guide', t: 'Per-sport length overrides', h: 'Advanced: JSON of sport → seconds (e.g. motorsport = 3 h). Used when a league has no per-league override.', ty: 'json' },
  epg_past_window_h: { g: 'Guide', t: 'Guide history (hours)', h: 'How many hours of past guide data to keep.', ty: 'int' },
  epg_future_window_d: { g: 'Guide', t: 'Guide lookahead (days)', h: 'How many days of upcoming guide data to keep.', ty: 'int' },
  epg_max_programmes: { g: 'Guide', t: 'Guide safety cap', h: 'Max programmes stored per source to prevent runaway database growth.', ty: 'int' },
  epg_repick_enabled: { g: 'Guide', t: 'Guide-match channel pick', h: 'Within ~1 hour of start, re-check each mapped channel’s guide — refreshing the source’s EPG first if it’s more than 12 hours old — and record from the channel that actually shows the event. Manual channel choices are never moved.', ty: 'bool' },
  epg_auto_sync_enabled: { g: 'Guide', t: 'Auto-sync EPG daily', h: 'Automatically refresh every source’s TV guide once a day at the time below.', ty: 'bool' },
  epg_auto_sync_time: { g: 'Guide', t: 'EPG sync time', h: 'Time of day (24-hour) to auto-refresh the guide, in the timezone below.', ty: 'time' },
  epg_auto_sync_offset_minutes: { g: 'Guide', t: 'EPG sync timezone', h: 'Timezone for the sync time — a fixed offset (no daylight-saving adjustment).', ty: 'select', opts: [
    { v: '600', l: 'UTC+10:00 — Brisbane, Sydney, Melbourne' },
    { v: '570', l: 'UTC+09:30 — Adelaide' },
    { v: '480', l: 'UTC+08:00 — Perth' },
    { v: '720', l: 'UTC+12:00 — Auckland' },
    { v: '0', l: 'UTC+00:00 — UTC, London' },
    { v: '-300', l: 'UTC-05:00 — New York' },
    { v: '-480', l: 'UTC-08:00 — Los Angeles' },
  ] },
  thesportsdb_api_key: { g: 'TheSportsDB', t: 'TheSportsDB API key', h: 'Your premium TheSportsDB (v2) key — required to browse leagues and sync fixtures.', ty: 'text' },
  ha_webhook_url: { g: 'Integrations', t: 'Home Assistant webhook', h: 'Webhook URL to push recording state changes to Home Assistant. Blank = off.', ty: 'url' },
  public_base_url: { g: 'Integrations', t: 'Public base URL', h: 'Externally-reachable URL of this DVarr (e.g. https://dvr.example.com), used to build the away-from-home calendar-feed link. Blank = off.', ty: 'url' },
  default_channel_source_filter: { g: 'Display', t: 'Default channel filter', h: 'Which source’s channels to show by default (“all” or a source id).', ty: 'text' },
  timezone_display: { g: 'Display', t: 'Display timezone', h: 'IANA timezone used to show times (e.g. Australia/Brisbane).', ty: 'text' },
  litestream_target: { g: 'Backups', t: 'Litestream backup target', h: 'Continuous database backup destination (e.g. s3://bucket/path). Blank = off.', ty: 'url' },
};
function settingField(k, v) {
  const m = SETTINGS_META[k] || { t: k, h: '', ty: (v === 'true' || v === 'false') ? 'bool' : 'text' };
  const id = 'set_' + k;
  if (m.ty === 'bool')
    return `<label class="set-row" style="display:flex;gap:10px;align-items:flex-start"><input type="checkbox" id="${id}" data-k="${esc(k)}" data-bool="1" ${v === 'true' ? 'checked' : ''} style="width:auto;margin-top:3px"/><span><b>${esc(m.t)}</b>${m.h ? `<div class="muted" style="font-size:12px">${esc(m.h)}</div>` : ''}</span></label>`;
  let input;
  if (m.ty === 'int') input = `<input type="number" id="${id}" data-k="${esc(k)}" value="${esc(v)}"/>`;
  else if (m.ty === 'time') input = `<input type="time" id="${id}" data-k="${esc(k)}" value="${esc(v)}"/>`;
  else if (m.ty === 'select') input = `<select id="${id}" data-k="${esc(k)}">${(m.opts || []).map(o => `<option value="${esc(o.v)}" ${String(v) === String(o.v) ? 'selected' : ''}>${esc(o.l)}</option>`).join('')}</select>`;
  else if (m.ty === 'url') input = `<input type="url" id="${id}" data-k="${esc(k)}" value="${esc(v)}" placeholder="blank = off"/>`;
  else if (m.ty === 'json') input = `<textarea id="${id}" data-k="${esc(k)}" data-json="1" rows="2" spellcheck="false" style="font-family:var(--mono,monospace);font-size:12px">${esc(v)}</textarea>`;
  else input = `<input type="text" id="${id}" data-k="${esc(k)}" value="${esc(v)}"/>`;
  return `<label class="set-row${m.ty === 'json' ? ' set-wide' : ''}"><b>${esc(m.t)}</b>${m.h ? `<div class="muted" style="font-size:12px;margin-bottom:4px">${esc(m.h)}</div>` : ''}${input}</label>`;
}
PAGES.settings = {
  title: 'Settings',
  async render(el) {
    const s = await api.get('/api/settings');
    // Internal bookkeeping keys — persisted server-side but not user settings; never render them.
    const HIDDEN = ['epg_auto_sync_last'];
    // Bucket every setting into one of the 5 tabs (unknown keys → Advanced, so a new backend setting is never hidden).
    const buckets = {}; SETTINGS_TABS.forEach(t => buckets[t] = []);
    Object.keys(SETTINGS_META).filter(k => k in s).forEach(k => buckets[GROUP_TAB[SETTINGS_META[k].g] || 'Advanced'].push(k));
    Object.keys(s).filter(k => !SETTINGS_META[k] && !HIDDEN.includes(k)).forEach(k => buckets['Advanced'].push(k));
    // "Plex" is a fixed info-only tab (no settings keys) whose pane is custom HTML, not the generic set-grid — appended
    // after the key-driven tabs so it always shows regardless of which settings exist.
    const PLEX_TAB = 'Plex';
    const tabs = [...SETTINGS_TABS.filter(t => buckets[t].length), PLEX_TAB];
    const nav = `<div class="tab-nav">${tabs.map((t, i) => `<button class="tab-btn${i === 0 ? ' active' : ''}" data-tab="${esc(t)}">${esc(t)}</button>`).join('')}</div>`;
    const panes = tabs.map((t, i) => `<div class="tab-pane${i === 0 ? ' active' : ''}" data-tab="${esc(t)}">${t === PLEX_TAB ? plexSettingsPane() : `<div class="set-grid">${buckets[t].map(k => settingField(k, s[k])).join('')}</div>`}</div>`).join('');
    el.innerHTML = `<div class="settings-wrap">${nav}${panes}
      <div class="row" style="margin:18px 0 28px"><button onclick="saveSettings()">Save settings</button><span id="setMsg" class="muted"></span></div></div>`;
    el.querySelectorAll('.tab-btn').forEach(b => b.addEventListener('click', () => {
      el.querySelectorAll('.tab-btn').forEach(x => x.classList.toggle('active', x === b));
      el.querySelectorAll('.tab-pane').forEach(p => p.classList.toggle('active', p.dataset.tab === b.dataset.tab));
    }));
  },
};

// Settings → Plex: info-only pane. The provider URL (location.origin + '/plex') + how the Custom Metadata Provider
// works + set-up steps, all grounded in PlexEndpoints.cs (the /plex agent) and MediaImportService.cs (the on-disk
// League/Season/event layout + .unsorted staging).
function plexSettingsPane() {
  const url = location.origin + '/plex';
  return `<div style="max-width:720px">
    <label class="field" style="margin-bottom:6px">Provider URL <span class="muted">(paste this into Plex)</span>
      <div class="copy-row"><input id="plexUrl" readonly value="${esc(url)}"/><button class="ghost sm" onclick="copyText($('#plexUrl').value,'#plexUrl')">Copy</button></div></label>
    <div class="muted" style="font-size:12px;margin-bottom:16px">Give Plex an address it can reach on the LAN — if you're viewing DVarr through the public domain, swap in the server's LAN IP (e.g. <span class="mono">http://192.168.x.x:1867/plex</span>).</div>

    <div class="note" style="margin-bottom:14px"><b>How it works.</b> DVarr files finished recordings into the media folder as <span class="mono">League / Season &lt;year&gt; / &lt;event&gt; …</span> (Sonarr/Plex-style), and exposes a Plex <b>Custom Metadata Provider</b> at <span class="mono">/plex</span>. When Plex scans that library with the DVarr provider selected, it asks DVarr to match each folder/file and DVarr answers with real event metadata — league poster/artwork, event title, date, and season/episode numbering — sourced from TheSportsDB. The <span class="mono">/plex</span> surface is deliberately login-exempt so a LAN Plex server can reach it without credentials (it serves public sports metadata only).</div>

    <b style="font-size:13px">Set-up</b>
    <ol class="plex-steps">
      <li>In Plex, create or edit a <b>TV Shows</b> library pointing at DVarr's media folder (where recordings are filed).</li>
      <li>Add the Provider URL above as a <b>custom metadata provider</b> for that library, then select the DVarr provider as the library's <b>agent</b>.</li>
      <li><b>Scan</b> the library — Plex matches each <span class="mono">League/Season &lt;year&gt;/&lt;event&gt;</span> folder against DVarr and pulls in the artwork, titles, dates and season/episode numbers.</li>
    </ol>
    <div class="note" style="margin-top:14px">Only <b>event-linked</b> recordings are filed and matchable — schedule them from a monitored league/event, or attach one to a game via <b>Import</b>. A manual recording with no event sits in a <span class="mono">.unsorted</span> folder that Plex ignores until you Import it.</div>
    <div class="muted" style="font-size:11px;margin-top:10px">Provider identifier: <span class="mono">tv.plex.agents.custom.dvarr.sports</span></div>
  </div>`;
}

function emptyBox(msg) { return `<div class="empty">${I.recordings}<div>${esc(msg)}</div></div>`; }

// =========================================================================
// Channel cascade (Source → Group → Channel, each with keyword search)
// =========================================================================
async function buildChannelCascade(host, prefill = {}) {
  const sources = await api.get('/api/sources');
  if (!sources.length) { host.innerHTML = `<div class="note">No sources yet — add one first (Sources page).</div>`; return; }
  // Default to the first ENABLED source so we never steer the user at an off-limits/disabled credential.
  const startSrc = prefill.sourceId != null ? String(prefill.sourceId) : String((sources.find(s => s.enabled) || sources[0]).id);
  host.innerHTML = `
    <label class="field">Source<select id="cascSrc">${sources.map(s => `<option value="${s.id}" ${String(s.id) === startSrc ? 'selected' : ''}>${esc(s.label)}${s.enabled ? '' : ' — disabled'}</option>`).join('')}</select></label>
    <label class="field">Group<input id="cascGrpQ" placeholder="filter groups (e.g. uk sports)…"/><select id="cascGrp"></select></label>
    <label class="field">Channel<input id="cascChQ" placeholder="search channels (keyword)…"/>
      <div id="cascChList" class="picklist" role="listbox" tabindex="0"></div>
      <input type="hidden" id="cascCh"/></label>`;
  let groups = [], chans = [];
  const renderGroups = () => {
    const q = $('#cascGrpQ').value;
    const f = q ? groups.filter(g => tokensMatch(g, q)) : groups;
    $('#cascGrp').innerHTML = `<option value="all">All groups${f.length ? ` (${f.length})` : ''}</option>` + f.slice(0, 3000).map(g => `<option value="${esc(g)}" ${g === prefill.group ? 'selected' : ''}>${esc(g)}</option>`).join('');
  };
  const renderChannels = () => {
    const q = $('#cascChQ').value;
    const f = q ? chans.filter(c => tokensMatch(c.name + ' ' + (c.group || ''), q)) : chans;
    const sel = $('#cascCh').value;
    const list = $('#cascChList');
    if (!f.length) { list.innerHTML = `<div class="muted" style="padding:8px 11px">(no channels)</div>`; return; }
    list.innerHTML = f.slice(0, 500).map(c => {
      const label = c.name + (c.group ? ` — ${c.group}` : '');
      return `<div class="pickrow${String(c.id) === String(sel) ? ' sel' : ''}" role="option" data-id="${c.id}" title="${esc(label)}">${esc(label)}</div>`;
    }).join('');
  };
  const loadGroups = async () => { groups = await api.get(`/api/channels/groups?source=${$('#cascSrc').value}`); renderGroups(); };
  const loadChannels = async () => { chans = await api.get(`/api/channels?source=${$('#cascSrc').value}&group=${encodeURIComponent($('#cascGrp').value)}&take=1000`); renderChannels(); };
  // Click a row → record its id in the hidden input and move the highlight (single-select listbox).
  $('#cascChList').onclick = (e) => {
    const row = e.target.closest('.pickrow'); if (!row || !row.dataset.id) return;
    $('#cascCh').value = row.dataset.id;
    [...$('#cascChList').children].forEach(r => r.classList.toggle('sel', r === row));
  };
  // Changing source/group invalidates the current channel choice (it may not exist in the new list).
  $('#cascSrc').onchange = async () => { $('#cascCh').value = ''; await loadGroups(); $('#cascGrp').value = 'all'; await loadChannels(); };
  $('#cascGrp').onchange = () => { $('#cascCh').value = ''; loadChannels(); };
  let gt; $('#cascGrpQ').oninput = () => { clearTimeout(gt); gt = setTimeout(renderGroups, 150); };
  let ct; $('#cascChQ').oninput = () => { clearTimeout(ct); ct = setTimeout(renderChannels, 200); };
  await loadGroups();
  if (prefill.group) {
    // Guarantee the prefilled group is selectable even if it sorts beyond the rendered slice (919+ groups),
    // otherwise setting .value silently no-ops and the group/channel don't fill in (guide click-to-schedule).
    if (![...$('#cascGrp').options].some(o => o.value === prefill.group))
      $('#cascGrp').insertAdjacentHTML('beforeend', `<option value="${esc(prefill.group)}">${esc(prefill.group)}</option>`);
    $('#cascGrp').value = prefill.group;
  }
  await loadChannels();
  // Guarantee the prefilled channel is selectable even if it sorts beyond the rendered slice (a group can have
  // >500 channels): if it's not in the list, prepend a synthetic row. Then set the hidden value + highlight it.
  if (prefill.channelId) {
    const list = $('#cascChList');
    if (![...list.children].some(r => String(r.dataset.id) === String(prefill.channelId))) {
      const label = prefill.channelName || ('channel ' + prefill.channelId);
      list.insertAdjacentHTML('afterbegin', `<div class="pickrow" role="option" data-id="${prefill.channelId}" title="${esc(label)}">${esc(label)}</div>`);
    }
    $('#cascCh').value = String(prefill.channelId);
    [...list.children].forEach(r => r.classList.toggle('sel', String(r.dataset.id) === String(prefill.channelId)));
  }
}

// =========================================================================
// Live preview (mpegts.js → DVarr byte-proxy; creds never reach the browser)
// =========================================================================
let _player = null, _hls = null;
function openPreview(channelId, name) {
  modal(`<h2 style="display:flex;justify-content:space-between;align-items:center;gap:12px">${esc(name)} <span class="tag busy">uses 1 stream slot</span></h2>
    <video id="pvVideo" controls autoplay playsinline muted style="width:100%;background:#000;border-radius:8px;max-height:64vh"></video>
    <div id="pvMsg" class="muted" style="margin-top:8px">Connecting…</div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Close</button></div>`, 'min(900px,94vw)');
  startPreview(channelId);
}
// Try the cheap direct path (mpegts.js: H.264/AAC, no transcode). On a CODEC error (HEVC/AC-3 — common on 4K/UHD
// channels) fall back to a server-side transcode (hls.js). On a network/busy error, just report it.
function startPreview(channelId) {
  const video = $('#pvVideo'), msg = $('#pvMsg');
  if (!video) return;
  if (typeof mpegts === 'undefined' || !mpegts.isSupported()) { startHls(channelId); return; }
  stopPreview();
  // ABSOLUTE url: with enableWorker the fetch runs in a Web Worker that can't resolve a relative path
  // ("Failed to parse URL from /api/preview/…") — which silently broke every preview.
  // Buffering: keep an IO stash + DON'T chase the live edge — latency-chasing with no stash underruns on the
  // smallest jitter, which is the "plays 1s then buffers" choppiness. Trade a few seconds of latency for smoothness.
  _player = mpegts.createPlayer({ type: 'mpegts', isLive: true, url: `${location.origin}/api/preview/${channelId}.ts` },
    { enableWorker: true, enableStashBuffer: true, stashInitialSize: 512 * 1024, liveBufferLatencyChasing: false, lazyLoad: false });
  _player.attachMediaElement(video);
  _player.on(mpegts.Events.ERROR, (type, detail, info) => {
    if (type === mpegts.ErrorTypes.MEDIA_ERROR) { startHls(channelId); return; } // codec not browser-playable → transcode
    const busy = info && (info.code === 409 || /conflict/i.test(info.msg || ''));
    msg.innerHTML = busy
      ? `<span style="color:var(--warn)">This source's single stream is busy</span> — a recording or another preview is using it. Close that and retry.`
      : `<span style="color:var(--warn)">Couldn't reach this channel</span> — it may be offline. <span class="muted">[${esc(detail || type)}]</span>`;
  });
  _player.load();
  video.play().catch(() => { });
  msg.textContent = 'Streaming live through DVarr (credentials stay server-side). Tap the video for volume / fullscreen.';
}
function startHls(channelId) {
  const video = $('#pvVideo'), msg = $('#pvMsg');
  if (!video) return;
  stopPreview(); // free the direct slot before the transcode acquires it
  msg.textContent = 'Transcoding this channel for your browser… (a few seconds)';
  const url = `/api/preview/${channelId}/hls/index.m3u8`;
  if (window.Hls && Hls.isSupported()) {
    _hls = new Hls({ liveSyncDurationCount: 3, manifestLoadingMaxRetry: 8, manifestLoadingRetryDelay: 1000, levelLoadingMaxRetry: 8 });
    _hls.loadSource(url);
    _hls.attachMedia(video);
    _hls.on(Hls.Events.MANIFEST_PARSED, () => { video.play().catch(() => { }); msg.textContent = 'Live (transcoded for your browser). Tap the video for volume / fullscreen.'; });
    _hls.on(Hls.Events.ERROR, (e, data) => {
      if (data && data.fatal) msg.innerHTML = `<span style="color:var(--warn)">Couldn't play this channel</span> — it may be offline, or the source's single stream is busy. <span class="muted">[${esc(data.type || '')}]</span>`;
    });
  } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
    video.src = url; video.play().catch(() => { }); msg.textContent = 'Live (transcoded).';
  } else {
    msg.textContent = 'Your browser cannot play live streams. Try Chrome/Edge.';
  }
}
function stopPreview() {
  if (_player) { try { _player.destroy(); } catch { } _player = null; }
  if (_hls) { try { _hls.destroy(); } catch { } _hls = null; }
}

// =========================================================================
// actions (global)
// =========================================================================
function openTestModal() {
  modal(`<h2>Test recording</h2><div class="fields">
    <label class="field">Stream URL (public test stream — no provider contact)<input id="mUrl" value="https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8"/></label>
    <label class="field">Name<input id="mName" value="Test capture"/></label>
    <label class="field">Duration (minutes)<input id="mMin" type="number" value="2" min="1" max="180"/></label>
    </div><div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitTest()">Start recording</button></div>`);
}
async function submitTest() {
  const url = $('#mUrl').value, name = $('#mName').value, minutes = parseInt($('#mMin').value) || 2;
  if (!url) return toast('URL required', 'err');
  closeModals();
  const r = await api.post('/api/test/recording', { url, name, minutes });
  if (r.error) return toast(r.error, 'err');
  toast(`Scheduled test recording #${r.id} (${r.minutes} min)`, 'ok');
  location.hash = '#/recordings';
}

async function openScheduleModal(prefill = {}) {
  modal(`<h2>Schedule recording</h2>
    <div class="fields" id="schedCascade"></div>
    <div class="fields" style="margin-top:14px;border-top:1px solid var(--line);padding-top:14px">
      <label class="field">Recording name<input id="mName" value="${esc(prefill.title || '')}" placeholder="e.g. Adelaide Crows vs Carlton"/></label>
      <label class="field" style="flex-direction:row;align-items:center;gap:8px"><input id="mMatch" type="checkbox" style="width:auto" ${prefill.title ? 'checked' : ''}/> Match this name to TheSportsDB and rename for Plex when it finishes</label>
      <div class="row" style="gap:10px">
        <label class="field grow">Start (local)<input id="mStart" type="datetime-local" value="${prefill.startLocal || nowLocalInput()}"/></label>
        <label class="field grow">Duration (minutes)<input id="mDur" type="number" value="${prefill.durationMin || 120}" min="1"/></label>
      </div>
    </div>
    <div id="planBadge" class="note" style="display:none;margin-top:12px"></div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitSchedule()">Schedule</button></div>`, 'min(560px,94vw)');
  await buildChannelCascade($('#schedCascade'), prefill);
  // Live "where will this land?" badge (free login / spread to the other login / conflict).
  $('#cascChList')?.addEventListener('click', () => setTimeout(updatePlanBadge, 0));
  $('#mStart')?.addEventListener('change', updatePlanBadge);
  $('#mDur')?.addEventListener('input', () => { clearTimeout(window._pbT); window._pbT = setTimeout(updatePlanBadge, 350); });
  updatePlanBadge();
}

// Calls the read-only plan-preview endpoint and reflects the credit-aware decision in the modal.
async function updatePlanBadge() {
  const badge = $('#planBadge'); if (!badge) return;
  const channelId = parseInt($('#cascCh')?.value);
  const startStr = $('#mStart')?.value; const dur = parseInt($('#mDur')?.value) || 0;
  if (!channelId || !startStr || dur <= 0) { badge.style.display = 'none'; return; }
  const start = Math.floor(new Date(startStr).getTime() / 1000);
  try {
    const r = await api.get(`/api/recordings/plan-preview?channelId=${channelId}&startUtc=${start}&endUtc=${start + dur * 60}`);
    badge.style.display = 'block';
    badge.className = 'note' + (r.conflict ? ' warn' : '');
    badge.textContent = r.badge || '';
  } catch { badge.style.display = 'none'; }
}
async function submitSchedule() {
  const channelId = parseInt($('#cascCh').value);
  if (!channelId) return toast('Pick a channel', 'err');
  const start = Math.floor(new Date($('#mStart').value).getTime() / 1000);
  if (!start) return toast('Pick a start time', 'err');
  const dur = (parseInt($('#mDur').value) || 120) * 60;
  const name = $('#mName').value.trim();
  const matchQuery = ($('#mMatch').checked && name) ? name : null;
  closeModals();
  const r = await api.post('/api/recordings', { channelId, startUtc: start, endUtc: start + dur, title: name || undefined, matchQuery });
  if (r.error) toast(r.error, 'err'); else { toast(`Scheduled recording #${r.id}`, 'ok'); location.hash = '#/recordings'; }
}
function scheduleFor(channelId) {
  const c = (window._chanRows || {})[channelId] || {};
  openScheduleModal({ sourceId: c.sourceId, group: c.group, channelId, channelName: c.name });
}

// Manual import: sort a staged (.unsorted) recording onto a TheSportsDB game — Sport → League → Game.
async function openImportModal(id, startUtc, title) {
  const sports = await api.get('/api/tsdb/sports');
  const dateStr = new Date(startUtc * 1000).toISOString().slice(0, 10);
  modal(`<h2>Import recording</h2>
    <div class="muted" style="margin-bottom:10px">${esc(title || ('Recording #' + id))} · ${brisbane(startUtc)}</div>
    <div class="fields">
      <label class="field">Sport<select id="impSport"><option value="">— pick a sport —</option>${sports.map(s => `<option value="${esc(s.name)}">${esc(s.name)}</option>`).join('')}</select></label>
      <label class="field">League<input id="impLeagueQ" placeholder="filter leagues (keyword)…" disabled/><select id="impLeague" disabled><option value="">— pick a sport first —</option></select></label>
      <label class="field">Game<input id="impGameQ" placeholder="search games (keyword)…" disabled/>
        <div id="impGameList" class="picklist" role="listbox" tabindex="0"></div>
        <input type="hidden" id="impGame"/></label>
    </div>
    <div id="impMsg" class="muted" style="margin-top:8px;font-size:12px"></div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button id="impGo" onclick="submitImport(${id})" disabled>Import</button></div>`, 'min(560px,94vw)');
  window._impDate = dateStr;
  let leagues = [], games = [];
  // League: a keyword filter narrows the <select> option list (mirrors the schedule Group step).
  const renderLeagues = () => {
    const q = $('#impLeagueQ').value;
    const f = q ? leagues.filter(l => tokensMatch(l.name, q)) : leagues;
    // Keep a made selection when the filter still includes it — rebuilding options silently resets the select
    // with no change event, which would desync the already-loaded game list from the (now empty) league value.
    const cur = $('#impLeague').value;
    $('#impLeague').innerHTML = `<option value="">— pick a league${f.length ? ` (${f.length})` : ''} —</option>` + f.map(l => `<option value="${esc(l.id)}">${esc(l.name)}</option>`).join('');
    if (cur && f.some(l => String(l.id) === cur)) $('#impLeague').value = cur;
  };
  // Game: a keyword search + custom ellipsis listbox writing the chosen id into #impGame (mirrors the schedule Channel step).
  const renderGames = () => {
    const q = $('#impGameQ').value;
    const f = q ? games.filter(g => tokensMatch(g.title + ' ' + (g.date ? brisbane(g.date) : ''), q)) : games;
    const sel = $('#impGame').value;
    const list = $('#impGameList');
    if (!f.length) { list.innerHTML = `<div class="muted" style="padding:8px 11px">(no games)</div>`; return; }
    list.innerHTML = f.slice(0, 500).map(g => {
      const label = g.title + (g.date ? ` — ${brisbane(g.date)}` : '');
      return `<div class="pickrow${String(g.id) === String(sel) ? ' sel' : ''}" role="option" data-id="${g.id}" title="${esc(label)}">${esc(label)}</div>`;
    }).join('');
  };
  const clearGames = () => { games = []; $('#impGame').value = ''; $('#impGameQ').value = ''; $('#impGameQ').disabled = true; $('#impGameList').innerHTML = `<div class="muted" style="padding:8px 11px">— pick a league first —</div>`; $('#impGo').disabled = true; };
  clearGames();
  $('#impSport').onchange = async () => {
    const sport = $('#impSport').value;
    leagues = []; $('#impLeagueQ').value = ''; clearGames(); $('#impMsg').textContent = '';
    if (!sport) { $('#impLeagueQ').disabled = true; $('#impLeague').disabled = true; $('#impLeague').innerHTML = '<option value="">— pick a sport first —</option>'; return; }
    $('#impLeagueQ').disabled = true; $('#impLeague').disabled = true; $('#impLeague').innerHTML = '<option value="">loading…</option>';
    leagues = await api.get(`/api/tsdb/leagues?sport=${encodeURIComponent(sport)}`);
    renderLeagues();
    $('#impLeagueQ').disabled = false; $('#impLeague').disabled = false;
  };
  $('#impLeague').onchange = async () => {
    const leagueId = $('#impLeague').value;
    clearGames(); $('#impMsg').textContent = '';
    if (!leagueId) return;
    $('#impGameList').innerHTML = `<div class="muted" style="padding:8px 11px">loading…</div>`;
    games = await api.get(`/api/import/events?leagueId=${encodeURIComponent(leagueId)}&date=${window._impDate}`);
    if (!games.length) { $('#impGameList').innerHTML = `<div class="muted" style="padding:8px 11px">(no games near this date)</div>`; $('#impMsg').textContent = 'No games for that league near the recording date — try another league.'; return; }
    $('#impGameQ').disabled = false;
    renderGames();
  };
  // Click a row → record its id in the hidden input, move the highlight, enable Import (single-select listbox).
  $('#impGameList').onclick = (e) => {
    const row = e.target.closest('.pickrow'); if (!row || !row.dataset.id) return;
    $('#impGame').value = row.dataset.id;
    [...$('#impGameList').children].forEach(r => r.classList.toggle('sel', r === row));
    $('#impGo').disabled = false;
  };
  let lt; $('#impLeagueQ').oninput = () => { clearTimeout(lt); lt = setTimeout(renderLeagues, 150); };
  let gt; $('#impGameQ').oninput = () => { clearTimeout(gt); gt = setTimeout(renderGames, 200); };
}
async function submitImport(id) {
  const leagueId = $('#impLeague').value, eventId = $('#impGame').value;
  if (!leagueId || !eventId) return toast('Pick a sport, league and game', 'err');
  $('#impGo').disabled = true; $('#impMsg').textContent = 'Filing…';
  const r = await api.post(`/api/recordings/${id}/import`, { leagueId, eventId });
  if (r.error) { toast(r.error, 'err'); $('#impGo').disabled = false; $('#impMsg').textContent = ''; }
  else { toast('Imported into the library', 'ok'); closeModals(); render(); }
}
async function startRec(id) { const r = await api.post(`/api/recordings/${id}/start`); if (r.error) toast(r.error, 'err'); else toast(r.started ? 'Starting…' : 'Already running', 'ok'); render(); }
async function stopRec(id) { const r = await api.post(`/api/recordings/${id}/stop`); toast(r.cancelled ? 'Cancelled' : r.stopping ? 'Stopping…' : 'No change', r.error ? 'err' : 'ok'); render(); }
async function reresolveRec(id) { const r = await api.post(`/api/recordings/${id}/resolve`); if (r.error) toast(r.error, 'err'); else toast(r.changed ? `Re-resolved → ${r.channel}` : `Already on ${r.channel}`, 'ok'); render(); }
async function reresolveLeague(id) { const r = await api.post(`/api/leagues/${id}/reresolve`); if (r.error) toast(r.error, 'err'); else toast(`Re-resolved ${r.updated} scheduled recording${r.updated === 1 ? '' : 's'}${r.changed ? ` (${r.changed} changed channel)` : ''}`, 'ok'); render(); }
async function delRec(id) { if (!confirm('Delete this recording?')) return; const r = await api.del(`/api/recordings/${id}`); if (r.error) return toast(r.error, 'err'); toast('Deleted'); render(); }
function ingest(id, label) {
  modal(`<h2>Ingest channels — ${esc(label)}</h2>
    <div class="note warn">This contacts your IPTV provider and uses <b>${esc(label)}</b>'s single stream slot. Only proceed if you're not using that stream right now.</div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="doIngest(${id})">Contact provider &amp; ingest</button></div>`);
}
async function doIngest(id) {
  closeModals();
  toast('Ingesting…');
  const r = await api.post(`/api/sources/${id}/ingest`);
  if (r.ok) toast(`Ingested ${r.total} channels (${r.added} new)`, 'ok'); else toast(`Failed: ${r.error}`, 'err');
  render();
}
function syncEpg(id, label) {
  modal(`<h2>Sync EPG — ${esc(label)}</h2><div class="note warn">Pulls the full guide (provider xmltv, or this source's external EPG URL). A large external EPG can take a minute. Proceed?</div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="doSyncEpg(${id})">Sync EPG</button></div>`);
}
async function doSyncEpg(id) {
  closeModals();
  toast('Syncing EPG… (large guides take a minute)');
  const r = await api.post(`/api/sources/${id}/epg`);
  if (r.ok) toast(`EPG: ${r.programmes} programmes across ${r.channelsMatched} channels${r.truncated ? ' — hit the safety cap (raise epg_max_programmes in Settings)' : ''}`, r.truncated ? '' : 'ok'); else toast(`EPG failed: ${r.error}`, 'err');
  render();
}
function openSourceModal(id) {
  const x = id != null ? (window._sources || []).find(s => s.id === id) : null;
  const edit = !!x;
  const ck = v => v ? 'checked' : '';
  const sel = (a, b) => a === b ? 'selected' : '';
  modal(`<h2>${edit ? 'Edit' : 'Add'} source</h2>
    <div class="fields" style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
      <label class="field">Label<input id="sLabel" value="${esc(x?.label || '')}" placeholder="e.g. My IPTV (login 2)"/></label>
      <label class="field">Type<select id="sType"><option ${sel(x?.type, 'm3u') ? '' : 'selected'}>xtream</option><option ${sel(x?.type, 'm3u')}>m3u</option></select></label>
      <label class="field">Protocol<select id="sProto"><option ${x?.protocol === 'https' ? '' : 'selected'}>http</option><option ${sel(x?.protocol, 'https')}>https</option></select></label>
      <label class="field">Host<input id="sHost" value="${esc(x?.host || '')}" placeholder="provider.example.com"/></label>
      <label class="field">Port<input id="sPort" type="number" value="${x?.port || 0}"/></label>
      <label class="field">Max streams<input id="sMax" type="number" value="${x?.maxStreams || 1}" title="Provider enforces 1 per login"/></label>
      <label class="field">Username${edit ? ' <span class="muted">(blank = keep)</span>' : ''}<input id="sUser" value="" placeholder="${edit ? esc(x.username) : ''}"/></label>
      <label class="field">Password${edit ? ' <span class="muted">(blank = keep)</span>' : ''}<input id="sPass" type="password" placeholder="${edit ? '••••••' : ''}"/></label>
      <label class="field" style="grid-column:1/3">External EPG URL (optional)<input id="sEpg" value="${esc(x?.epgUrl || '')}" placeholder="https://…/epg.xml.gz"/></label>
      <label class="field" style="grid-column:1/3;flex-direction:row;align-items:center;gap:8px"><input id="sEpgOv" type="checkbox" ${ck(x?.epgOverride)} style="width:auto"/> Override the source's EPG with the external EPG above</label>
      <label class="field" style="grid-column:1/3">User-Agent (optional)<input id="sUa" value="${esc(x?.userAgent || '')}" placeholder="blank = VLC default; set if your provider requires a specific UA"/></label>
      <label class="field" style="flex-direction:row;align-items:center;gap:8px"><input id="sEnabled" type="checkbox" ${edit ? ck(x.enabled) : 'checked'} style="width:auto"/> Enabled</label>
    </div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitSource(${edit ? x.id : 'null'})">${edit ? 'Save' : 'Add'} source</button></div>`, 'min(620px,94vw)');
}
async function submitSource(id) {
  const body = {
    label: $('#sLabel').value, type: $('#sType').value, protocol: $('#sProto').value,
    host: $('#sHost').value, port: parseInt($('#sPort').value) || 0, maxStreams: parseInt($('#sMax').value) || 1,
    username: $('#sUser').value, password: $('#sPass').value, userAgent: $('#sUa').value,
    epgUrl: $('#sEpg').value, epgOverride: $('#sEpgOv').checked, enabled: $('#sEnabled').checked,
  };
  closeModals();
  if (id == null) { const r = await api.post('/api/sources', body); toast(r.error ? r.error : `Source added (#${r.id})`, r.error ? 'err' : 'ok'); }
  else { const r = await api.put('/api/sources/' + id, body); toast(r.error ? r.error : 'Source saved', r.error ? 'err' : 'ok'); }
  render();
}
async function deleteSource(id, label) {
  if (!confirm(`Delete source “${label}”?\nThis removes its channels and EPG (recordings are kept).`)) return;
  const r = await api.del('/api/sources/' + id);
  if (r.error) toast(r.error, 'err'); else { toast('Source deleted', 'ok'); render(); }
}
async function saveSettings() {
  const vals = {};
  let badJson = null;
  document.querySelectorAll('#view [data-k]').forEach(i => {
    if (i.dataset.bool) { vals[i.dataset.k] = i.checked ? 'true' : 'false'; return; }
    if (i.dataset.json) { try { JSON.parse(i.value); } catch { badJson = i.dataset.k; } }
    vals[i.dataset.k] = i.value;
  });
  if (badJson) { toast(`“${(SETTINGS_META[badJson] || {}).t || badJson}” isn’t valid JSON`, 'err'); return; }
  const r = await api.put('/api/settings', vals);
  if (r.error) { toast(r.error, 'err'); return; }
  const m = $('#setMsg'); if (m) { m.textContent = 'saved ✓'; setTimeout(() => { const e = $('#setMsg'); if (e) e.textContent = ''; }, 1800); }
}

// ---- leagues (TheSportsDB pickers) / events / mappings actions ----
async function openLeagueModal(id) {
  const x = id != null ? (window._leagues || []).find(l => l.id === id) : null;
  const edit = !!x;
  modal(`<h2>${edit ? 'Edit' : 'Add'} league</h2>
    <div id="lHeader" class="lg-modal-head"></div>
    <div class="fields">
      <label class="field">Sport<select id="lSport"><option>Loading…</option></select></label>
      <label class="field">League <span class="muted">(search)</span><input id="lLeagueQ" placeholder="e.g. AFL, NRL, supercars, premier league…"/><select id="lLeague" size="6"><option>Pick a sport first…</option></select></label>
      <label class="field">…or paste a TheSportsDB league id <span class="muted">(for anything not listed)</span><input id="lManualId" value="${esc(x?.externalLeagueId || '')}" placeholder="e.g. 4456"/></label>
      <label class="field">Auto-schedule horizon (days)<input id="lHorizon" type="number" value="${x?.scheduleHorizonDays || 14}"/></label>
      <label class="field">Recording stop<select id="lAutoStop">
        <option value="auto" ${(x?.autoStopMode || 'auto') === 'fixed' ? '' : 'selected'}>Auto — extend while the event is still live (via TheSportsDB)</option>
        <option value="fixed" ${(x?.autoStopMode || 'auto') === 'fixed' ? 'selected' : ''}>Fixed window</option>
      </select></label>
      <label class="field" id="lAutoStopMaxWrap" style="${(x?.autoStopMode || 'auto') === 'fixed' ? 'display:none' : ''}">Max extension (minutes)<input id="lAutoStopMax" type="number" min="0" placeholder="60" value="${x?.autoStopMaxExtendS ? Math.round(x.autoStopMaxExtendS / 60) : ''}"/></label>
      <label class="field">Calendar colour<input type="hidden" id="lColor" value="${esc(x?.color || '')}"/>
        <div class="swatches" id="lSwatches">${LEAGUE_COLORS.map(c => `<span class="swatch${(x?.color || '').toLowerCase() === c ? ' sel' : ''}" data-c="${c}" style="background:${c}" title="${c}"></span>`).join('')}</div></label>
      <label class="field" style="flex-direction:row;align-items:center;gap:8px"><input id="lMon" type="checkbox" ${(!x || x.monitored) ? 'checked' : ''} style="width:auto"/> Monitored — auto-record this league's events</label>
    </div>
    <div id="lTeamsWrap" style="display:none;margin-top:14px;border-top:1px solid var(--line);padding-top:12px">
      <div style="display:flex;align-items:center;justify-content:space-between;gap:10px;margin-bottom:6px">
        <b style="font-size:13px">Teams to follow</b>
        <span><button type="button" class="ghost sm" id="lTeamsAll">All</button> <button type="button" class="ghost sm" id="lTeamsNone">None</button></span>
      </div>
      <div class="muted" style="font-size:12px;margin-bottom:8px">Tick the teams you want to record. Tick <b>none</b> (or All) to record <b>every</b> match in the league.</div>
      <div id="lTeams" class="team-grid"></div>
    </div>
    <div id="lSessionsWrap" style="display:none;margin-top:14px;border-top:1px solid var(--line);padding-top:12px">
      <div style="display:flex;align-items:center;justify-content:space-between;gap:10px;margin-bottom:6px">
        <b style="font-size:13px">Sessions to record</b>
        <span><button type="button" class="ghost sm" id="lSessAll">All</button> <button type="button" class="ghost sm" id="lSessNone">None</button></span>
      </div>
      <div class="muted" style="font-size:12px;margin-bottom:8px">Tick the sessions you want to record (e.g. just the Race &amp; Qualifying). Tick <b>none</b> (or All) to record <b>every</b> session.</div>
      <div id="lSessions" class="team-grid"></div>
    </div>
    <details id="lLengthWrap" style="margin-top:14px;border-top:1px solid var(--line);padding-top:12px"${(x?.eventDurationOverrideS || (x && x.sessionDurations && Object.keys(x.sessionDurations).length)) ? ' open' : ''}>
      <summary style="font-size:13px;font-weight:600;cursor:pointer">Event length (advanced)</summary>
      <div class="muted" style="font-size:11px;margin:8px 0">Assumed length when the provider gives no end time. Blank = use the sport / global default.</div>
      <label class="field" style="font-size:12px;max-width:340px"><span>Default length override <span class="muted">(minutes)</span></span><input id="lDuration" type="number" min="1" value="${x?.eventDurationOverrideS ? Math.round(x.eventDurationOverrideS / 60) : ''}" placeholder="e.g. 120"/></label>
      <div id="lSessDurBlock" style="display:none;margin-top:12px">
        <div style="font-size:12px;font-weight:600;margin-bottom:2px">Per-session overrides <span class="muted">(motorsport)</span></div>
        <div class="muted" style="font-size:11px;margin-bottom:8px">A length per session kind — overrides the default above for that session.</div>
        <div id="lSessDur" class="set-grid"></div>
      </div>
    </details>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitLeague(${edit ? x.id : 'null'})">${edit ? 'Save' : 'Add'} league</button></div>`, 'min(720px,96vw)');

  $('#lSwatches').querySelectorAll('.swatch').forEach(sw => sw.addEventListener('click', () => {
    $('#lSwatches').querySelectorAll('.swatch').forEach(o => o.classList.remove('sel'));
    sw.classList.add('sel'); $('#lColor').value = sw.dataset.c;
  }));
  $('#lTeamsAll').addEventListener('click', () => $('#lTeams').querySelectorAll('input').forEach(i => i.checked = true));
  $('#lTeamsNone').addEventListener('click', () => $('#lTeams').querySelectorAll('input').forEach(i => i.checked = false));
  $('#lSessAll').addEventListener('click', () => $('#lSessions').querySelectorAll('input').forEach(i => i.checked = true));
  $('#lSessNone').addEventListener('click', () => $('#lSessions').querySelectorAll('input').forEach(i => i.checked = false));
  // "Max extension" only applies to the auto stop mode — hide it for a fixed window.
  $('#lAutoStop').addEventListener('change', () => { $('#lAutoStopMaxWrap').style.display = $('#lAutoStop').value === 'auto' ? '' : 'none'; });
  // Mark a user-typed max-extension so onLeaguePicked's motorsport prefill never overwrites (or later clears) it.
  $('#lAutoStopMax').addEventListener('input', () => { const el = $('#lAutoStopMax'); el.dataset.userEdited = 'true'; delete el.dataset.prefilled; });

  const savedTeamIds = new Set((x?.monitoredTeams || []).map(t => String(t.id)));
  const savedSessions = new Set(x?.monitoredSessions || []);
  const savedSessionDur = x?.sessionDurations || {}; // {kind: seconds}

  // When a league is chosen (dropdown or pasted id), load its logo header + (for team sports) the team picker.
  let pickSeq = 0; // latest-request-wins: rapid league switching must not let a stale response repaint the pickers
  const onLeaguePicked = async (leagueId) => {
    const seq = ++pickSeq;
    if (!leagueId) { $('#lHeader').innerHTML = ''; $('#lTeamsWrap').style.display = 'none'; $('#lSessionsWrap').style.display = 'none'; $('#lSessDurBlock').style.display = 'none'; return; }
    $('#lHeader').innerHTML = '<span class="muted" style="font-size:12px">Loading league…</span>';
    const d = await api.get('/api/tsdb/league/' + encodeURIComponent(leagueId));
    if (seq !== pickSeq) return; // a newer pick superseded this response — drop it
    if (!d || d.error || !d.name) { $('#lHeader').innerHTML = '<span class="muted" style="font-size:12px">Couldn’t load that league id.</span>'; $('#lTeamsWrap').style.display = 'none'; $('#lSessionsWrap').style.display = 'none'; $('#lSessDurBlock').style.display = 'none'; return; }
    const art = d.badge || d.poster;
    $('#lHeader').innerHTML = `${art ? `<img src="${esc(art)}" alt="" class="lg-modal-badge"/>` : ''}<div><b>${esc(d.name)}</b><div class="muted" style="font-size:12px">${esc(d.sport || '')} · #${esc(String(d.id))}</div></div>`;
    const motorsport = d.motorsport === true;
    // Three exclusive cases: team sport → team picker only; motorsport → session picker + per-session lengths;
    // neither (UFC/boxing/wrestling) → clean base fields, no team picker and no session picker.
    if (d.teamSport && Array.isArray(d.teams) && d.teams.length) {
      $('#lTeamsWrap').style.display = '';
      $('#lTeams').innerHTML = d.teams.map(t => `<label class="team-pick" title="${esc(t.name)}"><input type="checkbox" data-id="${esc(String(t.id))}" data-name="${esc(t.name)}" ${savedTeamIds.has(String(t.id)) ? 'checked' : ''}/>${t.badge || t.logo ? `<img src="${esc(t.badge || t.logo)}" alt="" loading="lazy"/>` : '<span class="team-pick-dot"></span>'}<span>${esc(t.name)}</span></label>`).join('');
    } else { $('#lTeamsWrap').style.display = 'none'; $('#lTeams').innerHTML = ''; }
    // Motorsport ONLY: a session picker (which sessions to record) + an advanced per-session length section.
    if (motorsport && Array.isArray(d.sessionTypes) && d.sessionTypes.length) {
      $('#lSessionsWrap').style.display = '';
      $('#lSessDurBlock').style.display = '';
      $('#lSessions').innerHTML = d.sessionTypes.map(k => {
        const checked = edit ? savedSessions.has(k) : (k === 'Race' || k === 'Qualifying'); // new league → Race + Qualifying
        return `<label class="team-pick" title="${esc(k)}"><input type="checkbox" data-kind="${esc(k)}" ${checked ? 'checked' : ''}/><span class="team-pick-dot"></span><span>${esc(k)}</span></label>`;
      }).join('');
      // Built-in per-session default minutes (mirrors MotorsportSession.DefaultDurationS): support sessions ~1h, Race
      // and Testing 3h. The placeholder shows the effective default so the UI reflects what actually gets recorded.
      const sessDefaultMin = { 'Practice 1': 60, 'Practice 2': 60, 'Practice 3': 60, 'Sprint Qualifying': 60, 'Sprint': 60, 'Qualifying': 60, 'Race': 180, 'Testing': 180 };
      $('#lSessDur').innerHTML = d.sessionTypes.map(k => {
        const mins = savedSessionDur[k] ? Math.round(savedSessionDur[k] / 60) : '';
        const ph = sessDefaultMin[k] ? `default ${sessDefaultMin[k]}` : 'default';
        return `<label class="field" style="font-size:12px"><span>${esc(k)} <span class="muted">(min)</span></span><input type="number" min="1" data-kind="${esc(k)}" value="${mins}" placeholder="${esc(ph)}"/></label>`;
      }).join('');
    } else { $('#lSessionsWrap').style.display = 'none'; $('#lSessions').innerHTML = ''; $('#lSessDurBlock').style.display = 'none'; $('#lSessDur').innerHTML = ''; }
    // Max-extension prefill: motorsport events usually need ~120 min. Prefill 120 only when the field is empty and the
    // user hasn't typed their own value (and we're not clobbering a saved override on an edit). data-prefilled marks a
    // value WE injected so we can cleanly revert it back to the placeholder (60) when switching to a non-motorsport league.
    const maxIn = $('#lAutoStopMax');
    if (maxIn) {
      if (motorsport) {
        if (maxIn.value === '' && maxIn.dataset.userEdited !== 'true') { maxIn.value = '120'; maxIn.dataset.prefilled = 'true'; }
      } else if (maxIn.dataset.prefilled === 'true') {
        maxIn.value = ''; delete maxIn.dataset.prefilled; // revert only our own prefill → placeholder 60
      }
    }
  };

  let leagues = [];
  const renderLeagues = () => {
    const q = $('#lLeagueQ').value;
    const f = q ? leagues.filter(l => tokensMatch(`${l.name} ${l.alternate || ''} ${l.country || ''}`, q)) : leagues;
    $('#lLeague').innerHTML = f.slice(0, 500).map(l => `<option value="${esc(l.id)}" data-name="${esc(l.name)}" data-sport="${esc(l.sport)}" ${x?.externalLeagueId === l.id ? 'selected' : ''}>${esc(l.name)}${l.country ? ` (${esc(l.country)})` : ''}</option>`).join('') || '<option value="">(no leagues)</option>';
  };
  const loadLeagues = async () => {
    $('#lLeague').innerHTML = '<option>Loading…</option>';
    leagues = await api.get('/api/tsdb/leagues?sport=' + encodeURIComponent($('#lSport').value));
    if (!Array.isArray(leagues)) leagues = [];
    renderLeagues();
  };
  const sports = await api.get('/api/tsdb/sports');
  $('#lSport').innerHTML = (Array.isArray(sports) ? sports : []).map(s => `<option ${x?.sport === s.name ? 'selected' : ''}>${esc(s.name)}</option>`).join('') || '<option>(TheSportsDB unavailable)</option>';
  $('#lSport').onchange = loadLeagues;
  let lt; $('#lLeagueQ').oninput = () => { clearTimeout(lt); lt = setTimeout(renderLeagues, 150); };
  $('#lLeague').onchange = () => { $('#lManualId').value = ''; onLeaguePicked($('#lLeague').value); };
  let mt; $('#lManualId').oninput = () => { clearTimeout(mt); mt = setTimeout(() => onLeaguePicked($('#lManualId').value.trim()), 400); };
  await loadLeagues();
  if (x?.externalLeagueId) await onLeaguePicked(x.externalLeagueId); // edit (or pre-filled id) → load header + teams now
}
async function submitLeague(id) {
  const opt = $('#lLeague').selectedOptions[0];
  const manual = $('#lManualId').value.trim();
  const externalLeagueId = manual || $('#lLeague').value;
  if (!externalLeagueId) return toast('Pick a league or paste a TheSportsDB id', 'err');
  // Team-follow: the ticked teams (empty = follow every match). Only meaningful when the picker is shown (team sports).
  const teamsShown = $('#lTeamsWrap') && $('#lTeamsWrap').style.display !== 'none';
  const monitoredTeams = teamsShown
    ? [...$('#lTeams').querySelectorAll('input:checked')].map(i => ({ id: i.dataset.id, name: i.dataset.name }))
    : [];
  // Session-follow (motorsport): ticked session kinds (empty = every session) + per-session length overrides (→ seconds).
  const sessionsShown = $('#lSessionsWrap') && $('#lSessionsWrap').style.display !== 'none';
  const monitoredSessions = sessionsShown
    ? [...$('#lSessions').querySelectorAll('input:checked')].map(i => i.dataset.kind)
    : [];
  const sessionDurations = {};
  if (sessionsShown) [...$('#lSessDur').querySelectorAll('input')].forEach(i => { const m = parseInt(i.value); if (m > 0) sessionDurations[i.dataset.kind] = m * 60; });
  // For a manually-pasted id, let the server fill name/sport from TheSportsDB (lookup by id).
  const body = {
    externalLeagueId,
    name: manual ? undefined : opt?.dataset.name,
    sport: manual ? undefined : (opt?.dataset.sport || $('#lSport').value),
    scheduleHorizonDays: parseInt($('#lHorizon').value) || 14, monitored: $('#lMon').checked, color: $('#lColor').value || '',
    eventDurationOverrideS: (() => { const v = parseInt($('#lDuration').value); return v > 0 ? v * 60 : 0; })(),
    autoStopMode: $('#lAutoStop').value,
    autoStopMaxExtendS: (parseInt($('#lAutoStopMax')?.value) > 0 ? parseInt($('#lAutoStopMax').value) * 60 : 0), // 0 = clear to default

    monitoredTeams, monitoredSessions, sessionDurations,
  };
  closeModals();
  if (id == null) { const r = await api.post('/api/leagues', body); toast(r.error ? r.error : 'League added', r.error ? 'err' : 'ok'); }
  else { const r = await api.put('/api/leagues/' + id, body); toast(r.error ? r.error : 'League saved', r.error ? 'err' : 'ok'); }
  render();
}
async function deleteLeague(id, name) { if (!confirm(`Delete league “${name}”? Removes its events & mappings.`)) return; const r = await api.del('/api/leagues/' + id); if (r.error) return toast(r.error, 'err'); toast('League deleted', 'ok'); render(); }
async function syncLeague(id) { toast('Syncing events…'); const r = await api.post('/api/leagues/' + id + '/sync'); toast(r.ok ? `Synced ${r.fetched} events (${r.added} new)` : `Sync failed: ${r.error}`, r.ok ? 'ok' : 'err'); render(); }

function openMapModal(leagueId, name) {
  const bg = modal(`<h2>Map channel — ${esc(name)}</h2>
    <div class="fields" id="mapCascade"></div>
    <div class="fields" style="margin-top:12px;border-top:1px solid var(--line);padding-top:12px">
      <label class="field">Rank — try order, 1 = first choice<input id="mRank" type="number" min="1" step="1" value="1"/></label>
      <label class="field" style="flex-direction:row;align-items:center;gap:8px"><input id="mPin" type="checkbox" checked style="width:auto"/> Pinned — your pick beats EPG guessing</label>
    </div>
    <div class="muted" style="font-size:12px;margin-top:8px;line-height:1.5">Rank 1 records first; if it won't open or drops out, DVarr falls back to rank 2, 3… (same provider login). Map several channels carrying the same event for resilience.</div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitMap(${leagueId})">Add mapping</button></div>`, 'min(560px,94vw)');
  buildChannelCascade($('#mapCascade'), {});
}
async function submitMap(leagueId) {
  const channelId = parseInt($('#cascCh').value);
  if (!channelId) return toast('Pick a channel', 'err');
  const body = { leagueId, channelId, rank: Math.max(1, parseInt($('#mRank').value) || 1), pinned: $('#mPin').checked };
  closeModals();
  const r = await api.post('/api/mappings', body); toast(r.error ? r.error : 'Mapping added', r.error ? 'err' : 'ok'); render();
}
async function deleteMapping(id) { const r = await api.del('/api/mappings/' + id); if (r.error) return toast(r.error, 'err'); toast('Mapping removed'); render(); }

function openEventModal() {
  const ls = window._leagues || [];
  if (!ls.length) { modal(`<h2>Add event</h2><div class="note">Create a league first (Leagues page).</div><div class="foot"><button class="ghost" onclick="closeModals()">Close</button></div>`); return; }
  modal(`<h2>Add event</h2><div class="fields">
    <label class="field">League<select id="eLeague">${ls.map(l => `<option value="${l.id}">${esc(l.name)}</option>`).join('')}</select></label>
    <label class="field">Title<input id="eTitle" placeholder="Team A vs Team B"/></label>
    <label class="field">Start (local)<input id="eStart" type="datetime-local" value="${nowLocalInput()}"/></label>
    <label class="field">Duration (minutes)<input id="eDur" type="number" value="120"/></label>
    <label class="field" style="flex-direction:row;align-items:center;gap:8px"><input id="eMon" type="checkbox" checked style="width:auto"/> Monitored (auto-record)</label>
    </div><div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitEvent()">Add event</button></div>`);
}
async function submitEvent() {
  const start = Math.floor(new Date($('#eStart').value).getTime() / 1000);
  const dur = (parseInt($('#eDur').value) || 120) * 60;
  const body = { leagueId: parseInt($('#eLeague').value), title: $('#eTitle').value, startUtc: start, endUtc: start + dur, monitored: $('#eMon').checked };
  closeModals();
  const r = await api.post('/api/events', body); toast(r.error ? r.error : 'Event added', r.error ? 'err' : 'ok'); render();
}
async function monitorEvent(id, mon) { const r = await api.put('/api/events/' + id + '/monitor', { monitored: mon }); if (r.error) return toast(r.error, 'err'); toast(mon ? 'Monitoring' : 'Unmonitored'); render(); }
async function resolvePreview(id) { const r = await api.get('/api/events/' + id + '/resolve'); if (r.ok) toast(`Resolves to: ${r.primary.channelName} (score ${Math.round(r.primary.score)})`, 'ok'); else toast(`Cannot resolve: ${r.reason}`, 'err'); }

// =========================================================================
// router
// =========================================================================
async function render() {
  const seq = (render._seq = (render._seq || 0) + 1); // navigation generation — a slow page must not paint over a newer one
  closeModals(); // navigating away must tear down any open modal — esp. a live preview holding the stream slot
  closeDrawer(); // picking a destination closes the mobile drawer
  const id = (location.hash.replace(/^#\//, '') || 'dashboard').split('?')[0];
  const page = PAGES[id] || PAGES.dashboard;
  document.querySelectorAll('.nav-item').forEach(a => a.classList.toggle('active', a.dataset.route === id));
  $('#pageTitle').textContent = page.title;
  // Desktop shows the page's inline action buttons unchanged; on phones the secondary (ghost) buttons hide and the
  // same actions reappear inside a topbar ⋯ menu (page.menuActions) so nothing is lost — just tidied away.
  $('#pageActions').innerHTML = (page.actions ? page.actions() : '') + (page.menuActions && page.menuActions.length ? kebab(page.menuActions) : '');
  setLive(null);
  const view = $('#view');
  view.innerHTML = '<div class="loading">Loading…</div>';
  try { await page.render(view); if (seq !== render._seq) return; } // a newer navigation started while loading — don't clobber it
  catch (e) { if (seq === render._seq) view.innerHTML = emptyBox('Failed to load this page: ' + e.message); }
}

window.addEventListener('hashchange', render);
// Esc closes (in order): an open ⋯ menu, then the nav drawer, then modals (which also stops any preview).
// On desktop neither a kebab nor the drawer can be open, so behaviour there is exactly "Esc closes modals" as before.
window.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  if (document.querySelector('.kebab-menu.open')) { closeKebabs(); return; }
  const app = $('.app');
  if (app && app.classList.contains('drawer-open')) { closeDrawer(); return; }
  closeModals();
});
window.render = render; window.openTestModal = openTestModal; window.submitTest = submitTest;
window.openScheduleModal = openScheduleModal; window.submitSchedule = submitSchedule; window.scheduleFor = scheduleFor; window.scheduleFromGuide = scheduleFromGuide;
window.openPreview = openPreview; window.stopRec = stopRec; window.startRec = startRec; window.delRec = delRec; window.reresolveRec = reresolveRec; window.reresolveLeague = reresolveLeague;
window.openImportModal = openImportModal; window.submitImport = submitImport;
window.ingest = ingest; window.doIngest = doIngest; window.saveSettings = saveSettings; window.closeModals = closeModals;
window.toggleKebab = toggleKebab; window.closeKebabs = closeKebabs;
window.syncEpg = syncEpg; window.doSyncEpg = doSyncEpg; window.openSourceModal = openSourceModal; window.submitSource = submitSource; window.deleteSource = deleteSource;
window.openLeagueModal = openLeagueModal; window.submitLeague = submitLeague; window.deleteLeague = deleteLeague; window.syncLeague = syncLeague;
window.openMapModal = openMapModal; window.submitMap = submitMap; window.deleteMapping = deleteMapping;
window.openEventModal = openEventModal; window.submitEvent = submitEvent; window.monitorEvent = monitorEvent; window.resolvePreview = resolvePreview; window.openCalEvent = openCalEvent;
window.copyText = copyText; window.openCalendarFeedModal = openCalendarFeedModal; window.saveCalendarPublicBase = saveCalendarPublicBase; window.editCalendarPublicBase = editCalendarPublicBase;
window.donate = donate;

buildNav();
// Mobile drawer wiring (hamburger toggles, scrim or a nav choice closes it).
(function wireDrawer() {
  const h = $('#hamburger'); if (h) h.addEventListener('click', toggleDrawer);
  const sc = $('#scrim'); if (sc) sc.addEventListener('click', closeDrawer);
  const menu = $('#menu'); if (menu) menu.addEventListener('click', e => { if (e.target.closest('.nav-item')) closeDrawer(); });
})();
if (!location.hash) location.hash = '#/dashboard';
render();
pollHealth();
setInterval(pollHealth, 5000);
connectSSE();
// Register the service worker so the app shell loads instantly (and works offline) once installed to the home screen.
if ('serviceWorker' in navigator) window.addEventListener('load', () => navigator.serviceWorker.register('/sw.js').catch(() => {}));
