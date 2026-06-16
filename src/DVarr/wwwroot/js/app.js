'use strict';
// DVarr UI — multi-page SPA (sidebar nav, hash routing) over the DVarr REST + SSE API.

const $ = (s, r = document) => r.querySelector(s);
// Tolerant of empty / non-JSON bodies (e.g. a 404 NotFound has no body) so handlers never throw on .json().
async function _json(res) { const t = await res.text(); try { return t ? JSON.parse(t) : {}; } catch { return { _raw: t }; } }
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
  bg.innerHTML = `<div class="modal"${width ? ` style="width:${width}"` : ''}>${html}</div>`;
  bg.addEventListener('click', e => { if (e.target === bg) closeModals(); });
  $('#modalRoot').appendChild(bg);
  return bg;
}
function closeModals() { stopPreview(); $('#modalRoot').replaceChildren(); }

// ---- live refresh wiring ----
let liveRefresh = null, liveTimer = null;
function setLive(fn) { liveRefresh = fn; }
function connectSSE() {
  const es = new EventSource('/api/stream/recordings');
  es.onmessage = () => { clearTimeout(liveTimer); liveTimer = setTimeout(() => liveRefresh && liveRefresh(), 150); };
  es.onerror = () => { es.close(); setTimeout(connectSSE, 3000); };
}

// ---- header / nav state ----
async function pollHealth() {
  try {
    const h = await api.get('/api/health');
    $('#stSlots').textContent = `${h.sources.free_credentials} / ${h.sources.total} free`;
    $('#stClock').textContent = h.time.brisbane.replace(/:\d\d /, ' ');
    $('#footDot').className = 'dot ' + (h.db.ok ? 'ok' : 'bad');
    $('#footTxt').textContent = `v${(h.version || '0').split('.').slice(0, 3).join('.')} · db ${h.db.ok ? 'ok' : 'down'}`;
    const badge = $('#menu .nav-item[data-route="recordings"] .nav-badge');
    if (badge) {
      if (h.recordings.active > 0) { badge.textContent = h.recordings.active; badge.className = 'nav-badge live'; badge.style.display = ''; }
      else { badge.style.display = 'none'; }
    }
  } catch { $('#footDot').className = 'dot bad'; $('#footTxt').textContent = 'offline'; }
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
  async render(el) {
    const draw = async () => {
      const now = Math.floor(Date.now() / 1000);
      const [recs, leagues, events] = await Promise.all([
        api.get('/api/recordings'),
        api.get('/api/leagues'),
        api.get(`/api/events?from=${now}`),
      ]);
      const live = recs.filter(r => ACTIVE.includes(r.state));
      const scheduled = recs.filter(r => r.state === 'Pending' && r.startUtc <= now + 86400).sort((a, b) => a.startUtc - b.startUtc); // next 24h only
      const upcoming = events.filter(e => e.start <= now + 86400).sort((a, b) => a.start - b.start); // next 24h only
      // 2×2 grid: row 1 = Recording now | Leagues, row 2 = Scheduled | Next 24h. Grid rows auto-equalise the two
      // cells' heights, so Scheduled and Next-24h match unless live recordings grow the Recording-now cell.
      el.innerHTML = `
        <div class="dash-grid">
          <div class="section dash-cell"><h2>Recording now ${live.length ? `<span class="pill s-recording">${live.length}</span>` : ''}</h2>${live.length ? recTable(live, true) : emptyBox('Nothing recording right now.')}</div>
          <div class="section dash-cell"><h2>Leagues ${leagues.length ? `<span class="pill s-done">${leagues.length}</span>` : ''}</h2>${leagues.length ? leagueChips(leagues) : emptyBox('No leagues yet — add one on the Leagues page.')}</div>
          <div class="section dash-cell"><h2>Scheduled — next 24h ${scheduled.length ? `<span class="pill s-pending">${scheduled.length}</span>` : ''}</h2>${scheduled.length ? recTable(scheduled, true) : emptyBox('Nothing scheduled in the next 24 hours.')}</div>
          <div class="section dash-cell"><h2>Next 24 hours ${upcoming.length ? `<span class="pill s-done">${upcoming.length}</span>` : ''}</h2>${upcoming.length ? upcomingEvents(upcoming, leagues) : emptyBox('No monitored events in the next 24 hours.')}</div>
        </div>`;
    };
    await draw();
    setLive(draw);
  },
};

// ---- Recordings ----
PAGES.recordings = {
  title: 'Recordings',
  actions: () => `<button onclick="openScheduleModal()">${I.plus} Schedule</button><button class="ghost" onclick="openTestModal()">${I.play} Test</button>`,
  async render(el) {
    el.innerHTML = `<div class="toolbar">
        <select id="recFilter"><option value="">All states</option><option>Recording</option><option>Pending</option><option>Done</option><option>NeedsAttention</option><option>Missed</option></select>
        <span class="muted" id="recCount"></span></div>
      <div id="recTableWrap"></div>`;
    const draw = async () => {
      const recs = await api.get('/api/recordings');
      const f = $('#recFilter').value;
      const rows = f === 'Recording' ? recs.filter(r => ACTIVE.includes(r.state)) : (f ? recs.filter(r => r.state === f) : recs);
      $('#recCount').textContent = `${rows.length} recording${rows.length === 1 ? '' : 's'}`;
      $('#recTableWrap').innerHTML = rows.length ? recTable(rows, true) : emptyBox('No recordings yet. Use “Schedule” or “Test”.');
    };
    $('#recFilter').addEventListener('change', draw);
    await draw();
    setLive(draw);
  },
};

function recTable(rows, withActions) {
  return `<table><thead><tr><th>Title</th><th>State</th><th>Channel</th><th>Source</th><th>Size</th><th>Window (Brisbane)</th>${withActions ? '<th></th>' : ''}</tr></thead><tbody>${rows.map(r => `
    <tr><td>${esc(r.title)}</td>
      <td><span class="pill ${sc(r.state)}">${r.state}</span>${r.attemptCount ? ` <span class="muted" title="relaunch/failover attempts">↻${r.attemptCount}</span>` : ''}</td>
      <td>${esc(r.channel)}</td><td class="muted">${esc(r.source)}</td>
      <td class="mono">${mb(r.bytesWritten)}</td>
      <td class="mono muted">${brisbane(r.startUtc)} – ${brisbane(r.endUtc)}</td>
      ${withActions ? `<td class="row" style="gap:6px">${r.state === 'Pending' || r.state === 'Conflict' ? `<button class="sm" onclick="startRec(${r.id})" title="Start this recording now (early/manual)">start</button>` : ''}${ACTIVE.includes(r.state) || r.state === 'Pending' || r.state === 'Conflict' ? `<button class="ghost sm" onclick="stopRec(${r.id})">stop</button>` : ''}<button class="danger sm" onclick="delRec(${r.id})">delete</button></td>` : ''}
    </tr>`).join('')}</tbody></table>`;
}
function notesList(notes) {
  const col = s => s === 'Critical' ? 'var(--crit)' : s === 'Warn' ? 'var(--warn)' : 'var(--dim)';
  return `<table><tbody>${notes.map(n => `<tr>
    <td class="mono muted" style="width:120px">${brisbane(n.tsUtc)}</td>
    <td style="width:120px"><span class="tag" style="color:${col(n.severity)}">${esc(n.kind)}</span></td>
    <td>${esc(n.message || (n.fromState ? n.fromState + ' → ' + n.toState : ''))}${n.recordingId ? ` <span class="muted">#${n.recordingId}</span>` : ''}</td></tr>`).join('')}</tbody></table>`;
}
function upcomingEvents(events, leagues) {
  return `<table><tbody>${events.map(e => {
    const c = leagueColor(leagues.find(l => l.id === e.leagueId) || e);
    return `<tr class="clickrow" onclick="openCalEvent(${e.id})">
      <td style="width:14px;padding-right:0"><span class="lg-dot" style="background:${c};margin:0"></span></td>
      <td class="mono muted" style="width:118px">${brisbane(e.start)}</td>
      <td>${esc(e.title)}<div class="muted" style="font-size:11px">${esc(e.league)}</div></td>
      <td style="width:90px">${e.monitored ? '<span class="tag ok">monitored</span>' : ''}</td></tr>`;
  }).join('')}</tbody></table>`;
}
function leagueChips(leagues) {
  return `<div class="league-chips">${leagues.map(l => `
    <div class="lchip" onclick="location.hash='#/calendar?league=${l.id}'" title="${jsq(l.name)}">
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
      $('#chWrap').innerHTML = rows.length ? `<table><thead><tr><th>Name</th><th>Group</th><th>Source</th><th>Quality</th><th></th></tr></thead><tbody>${rows.map(c => `
        <tr><td>${esc(c.name)}</td><td class="muted">${esc(c.group || '')}</td><td class="muted">${esc(c.sourceLabel)}</td><td class="mono muted">${esc(c.quality || '')}</td>
        <td class="row" style="gap:6px;flex-wrap:nowrap">
          <button class="play-btn sm" title="Watch live preview" onclick="openPreview(${c.id},'${jsq(c.name)}')">${I.play} Watch</button>
          <button class="ghost sm" onclick="scheduleFor(${c.id})">${I.plus} Schedule</button>
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
const GUIDE_PX_PER_HOUR = 200, GUIDE_CH_COL = 250;
PAGES.guide = {
  title: 'Guide',
  async render(el) {
    const sources = await api.get('/api/sources');
    if (!sources.length) { el.innerHTML = emptyBox('Add a source first, then ingest channels and sync EPG.'); return; }
    // Default to the source that actually has EPG (most programmes), so the guide opens on data — not an empty source.
    const epgSrc = sources.filter(s => s.enabled && s.programmes > 0).sort((a, b) => b.programmes - a.programmes)[0];
    const defSrc = epgSrc || sources.find(s => s.enabled) || sources[0];
    const state = { sourceId: String(defSrc.id), group: 'all', q: '', start: Math.floor(Date.now() / 1000) - 1800, hours: 12, groups: [] };
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
        <select id="gHours"><option value="6">6h</option><option value="12" selected>12h</option><option value="24">24h</option></select>
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

  wrap.innerHTML = `<div class="guide-scroll"><div class="guide-inner" style="width:${GUIDE_CH_COL + trackW}px">
    <div class="g-head"><div class="g-corner">${new Date(winStart * 1000).toLocaleDateString('en-AU', { timeZone: 'Australia/Brisbane', weekday: 'short', day: '2-digit', month: 'short' })}</div><div class="g-hours" style="width:${trackW}px">${ticks}</div></div>
    ${rows}
    ${(now >= winStart && now <= winEnd) ? `<div class="g-now" style="left:${GUIDE_CH_COL + xOf(now)}px"></div>` : ''}
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
  async render(el) {
    const s = await api.get('/api/sources');
    window._sources = s;
    el.innerHTML = `<div class="note warn">⚠ Your provider allows <b>one stream per login</b>. <b>Ingest</b>, <b>Sync EPG</b> and <b>Live preview</b> use that login's single slot — don't run them while recording on the same source.</div>
      <div class="section">${s.length ? `<table><thead><tr><th>Label</th><th>Type</th><th>Host</th><th>User</th><th>Slot</th><th>Channels</th><th>EPG</th><th></th></tr></thead><tbody>${s.map(x => `
        <tr><td><b>${esc(x.label)}</b></td><td class="muted">${esc(x.type)}</td><td class="mono muted">${esc(x.host)}${x.port ? ':' + x.port : ''}</td><td class="mono">${esc(x.username)}</td>
        <td><span class="tag ${x.slotFree ? 'ok' : 'busy'}">${x.slotFree ? 'free' : 'busy'}</span></td>
        <td class="mono">${x.channels}</td>
        <td class="mono muted">${x.programmes || 0}${x.epgOverride ? ' <span class="tag" title="external EPG override active">ext</span>' : ''}</td>
        <td class="row" style="gap:6px;flex-wrap:nowrap">
          <button class="ghost sm" onclick="ingest(${x.id},'${jsq(x.label)}')">Ingest</button>
          <button class="ghost sm" onclick="syncEpg(${x.id},'${jsq(x.label)}')">EPG</button>
          <button class="ghost sm" onclick="openSourceModal(${x.id})">Edit</button>
          <button class="danger sm" onclick="deleteSource(${x.id},'${jsq(x.label)}')">Del</button>
        </td></tr>`).join('')}</tbody></table>`
        : emptyBox('No sources configured. Click “Add source” to set one up.')}</div>`;
  },
};

// ---- Leagues + mappings ----
PAGES.leagues = {
  title: 'Leagues',
  actions: () => `<button onclick="openLeagueModal(null)">${I.plus} Add league</button><button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  async render(el) {
    const ls = await api.get('/api/leagues');
    window._leagues = ls;
    el.innerHTML = `<div class="note">A <b>monitored</b> league auto-records its events on its mapped channel(s). Leagues come from <b>TheSportsDB</b> — pick a sport then search the league; posters &amp; events sync automatically.</div>
      <div class="section"><h2>Leagues ${ls.length ? `<span class="pill s-done">${ls.length}</span>` : ''}</h2>${ls.length ? `<table><thead><tr><th></th><th>League</th><th>Sport</th><th>Events</th><th>Maps</th><th>Monitored</th><th></th></tr></thead><tbody>${ls.map(l => `
        <tr><td>${l.poster ? `<img class="lg-poster" src="${esc(l.poster)}" alt=""/>` : ''}</td>
        <td><span class="lg-dot" style="background:${leagueColor(l)}" title="calendar colour"></span><b>${esc(l.name)}</b>${l.externalLeagueId ? ` <span class="tag" title="TheSportsDB id">#${esc(l.externalLeagueId)}</span>` : ''}</td><td class="muted">${esc(l.sport)}</td>
        <td class="mono">${l.events}</td><td class="mono">${l.mappings}</td>
        <td><span class="tag ${l.monitored ? 'ok' : ''}">${l.monitored ? 'yes' : 'no'}</span></td>
        <td class="row" style="gap:6px;flex-wrap:nowrap">
          <button class="ghost sm" onclick="openMapModal(${l.id},'${jsq(l.name)}')">Map</button>
          <button class="ghost sm" onclick="syncLeague(${l.id})">Sync</button>
          <button class="ghost sm" onclick="location.hash='#/calendar?league=${l.id}'">Events</button>
          <button class="ghost sm" onclick="openLeagueModal(${l.id})">Edit</button>
          <button class="danger sm" onclick="deleteLeague(${l.id},'${jsq(l.name)}')">Del</button>
        </td></tr>`).join('')}</tbody></table>` : emptyBox('No leagues yet. Add one and pin a channel to start auto-recording.')}</div>
      <div class="section"><h2>Channel mappings</h2>
        <div class="note">
          <b>How mappings &amp; ranks work.</b> Each row maps a league to a channel. <b>Rank</b> is the order DVarr tries them — lowest first: <b>rank&nbsp;1</b> is the primary (first choice), rank&nbsp;2, 3… are fallbacks. When an event is due, DVarr records the best-ranked channel; if that channel <b>won't open or drops out</b>, it automatically walks down the list to the next working channel. Add a few channels that all carry the same event so there's always a backup.
          <div style="margin-top:6px"><b>★ Pinned</b> means your pick wins over EPG guesswork — a similar-looking guide entry can't hijack the recording. Unpinned mappings still work but let a strong EPG title match reorder them.</div>
          <div class="muted" style="margin-top:6px;font-size:12px;line-height:1.5">All fallbacks must be on the <b>same provider login</b> as the primary (one stream per login). With <b>content check</b> on (Settings), DVarr also fails over when a channel is alive but stuck on a dead <b>black or frozen</b> slate. It does <b>not</b> watch the picture to decide whether the “right” match is on — that relies on your ranks, the EPG, and pre/post-padding, which is exactly why a pre-show/intro is captured rather than skipped.</div>
        </div>
        <div id="mapsWrap" class="loading">…</div></div>`;
    const maps = await api.get('/api/mappings');
    $('#mapsWrap').innerHTML = maps.length ? `<table><thead><tr><th>League</th><th>Channel</th><th>Source</th><th>Rank</th><th>Pinned</th><th></th></tr></thead><tbody>${maps.map(m => {
      const lg = ls.find(x => x.id === m.leagueId);
      return `<tr><td>${esc(lg ? lg.name : '#' + m.leagueId)}</td><td>${esc(m.channel)}</td><td class="muted">${esc(m.source)}</td><td class="mono">${m.rank}</td><td>${m.pinned ? '★' : ''}</td><td><button class="danger sm" onclick="deleteMapping(${m.id})">Del</button></td></tr>`;
    }).join('')}</tbody></table>` : emptyBox('No mappings. Use “Map” on a league to pin a channel.');
  },
};

// ---- Calendar (monthly grid; events per day, colour-coded by league) ----
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const DOW = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
PAGES.calendar = {
  title: 'Calendar',
  actions: () => `<button onclick="openEventModal()">${I.plus} Add event</button><button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  async render(el) {
    const params = new URLSearchParams((location.hash.split('?')[1]) || '');
    const leagueFilter = params.get('league') || '';
    const ls = await api.get('/api/leagues'); window._leagues = ls;
    window._calEvents = {};
    const today = bneParts(Math.floor(Date.now() / 1000));
    const state = { y: today.y, m: today.m, league: leagueFilter };

    el.innerHTML = `
      <div class="toolbar cal-toolbar">
        <button class="ghost sm" id="calPrev">‹</button>
        <div id="calTitle" class="cal-title"></div>
        <button class="ghost sm" id="calNext">›</button>
        <button class="ghost sm" id="calToday">Today</button>
        <span class="grow"></span>
        <select id="calLeague"><option value="">All leagues</option>${ls.map(l => `<option value="${l.id}" ${String(l.id) === leagueFilter ? 'selected' : ''}>${esc(l.name)}</option>`).join('')}</select>
      </div>
      <div id="calGrid" class="loading">…</div>`;

    const draw = async () => {
      $('#calTitle').textContent = `${MONTHS[state.m]} ${state.y}`;
      const from = bneMonthStart(state.y, state.m);
      const to = bneMonthStart(state.m === 11 ? state.y + 1 : state.y, (state.m + 1) % 12) - 1;
      const events = await api.get(`/api/events?from=${from}&to=${to}${state.league ? `&leagueId=${state.league}` : ''}`);
      window._calEvents = {}; events.forEach(e => { window._calEvents[e.id] = e; });

      // bucket events by Brisbane day
      const byDay = {};
      events.forEach(e => { (byDay[bneDayKey(e.start)] ||= []).push(e); });
      Object.values(byDay).forEach(list => list.sort((a, b) => a.start - b.start));

      // build the month grid (weeks start Monday)
      const firstDow = (new Date(Date.UTC(state.y, state.m, 1)).getUTCDay() + 6) % 7; // 0=Mon
      const daysInMonth = new Date(Date.UTC(state.y, state.m + 1, 0)).getUTCDate();
      const todayKey = bneCellKey(today.y, today.m, today.d);

      let cells = '';
      for (let i = 0; i < firstDow; i++) cells += `<div class="cal-cell empty-cell"></div>`;
      for (let d = 1; d <= daysInMonth; d++) {
        const key = bneCellKey(state.y, state.m, d);
        const evs = byDay[key] || [];
        const isToday = key === todayKey;
        cells += `<div class="cal-cell${isToday ? ' today' : ''}">
          <div class="cal-daynum">${d}</div>
          <div class="cal-events">${evs.map(e => {
          const bg = leagueColor(ls.find(l => l.id === e.leagueId) || e);
          return `<div class="cal-ev" style="background:${bg};color:${textOn(bg)}" title="${esc(e.title)} · ${esc(e.league)} · ${brisbane(e.start)}" onclick="openCalEvent(${e.id})"><span class="cal-ev-t">${hhmm(e.start)}</span> ${esc(e.title)}</div>`;
        }).join('')}</div></div>`;
      }
      $('#calGrid').innerHTML = `<div class="cal-grid"><div class="cal-head">${DOW.map(d => `<div>${d}</div>`).join('')}</div><div class="cal-body">${cells}</div></div>`;
    };

    $('#calPrev').addEventListener('click', () => { if (state.m === 0) { state.m = 11; state.y--; } else state.m--; draw(); });
    $('#calNext').addEventListener('click', () => { if (state.m === 11) { state.m = 0; state.y++; } else state.m++; draw(); });
    $('#calToday').addEventListener('click', () => { const t = bneParts(Math.floor(Date.now() / 1000)); state.y = t.y; state.m = t.m; draw(); });
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

// ---- Activity ----
PAGES.activity = {
  title: 'Activity',
  actions: () => `<button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  async render(el) {
    const [notes, ticks] = await Promise.all([api.get('/api/notifications?take=60'), api.get('/api/ticks?take=15')]);
    el.innerHTML = `
      <div class="section"><h2>Notifications</h2>${notes.length ? notesList(notes) : emptyBox('No notifications yet.')}</div>
      <div class="section"><h2>Scheduler ticks</h2>${ticks.length ? `<table><thead><tr><th>Time</th><th>Examined</th><th>Started</th><th>Missed</th><th>Conflicts</th><th>ms</th></tr></thead><tbody>${ticks.map(t => `
        <tr><td class="mono muted">${brisbane(t.tickUtc)}</td><td class="mono">${t.recordingsExamined}</td><td class="mono">${t.started}</td><td class="mono">${t.missed}</td><td class="mono">${t.conflicts}</td><td class="mono muted">${t.durationMs}</td></tr>`).join('')}</tbody></table>`
        : emptyBox('Scheduler has not ticked yet.')}</div>`;
  },
};

// ---- Conflicts (credit-aware planning) ----
PAGES.conflicts = {
  title: 'Conflicts',
  actions: () => `<button class="ghost" onclick="render()">${I.refresh} Refresh</button>`,
  async render(el) {
    const [data, sources] = await Promise.all([api.get('/api/conflicts'), api.get('/api/sources')]);
    const creds = data.credentials || [], conflicts = data.conflicts || [];
    const enabled = (sources || []).filter(s => s.enabled);

    const conflictCards = conflicts.length ? conflicts.map(r => `
      <div class="card" style="border-color:var(--warn);margin-bottom:12px">
        <div style="min-width:0"><b>${esc(r.title || ('Recording #' + r.id))}</b>
          <div class="muted" style="font-size:12px">${brisbane(r.startUtc)} – ${hhmm(r.endUtc)} · ${esc(r.reason || 'conflict')}</div></div>
        <div class="row" style="margin-top:10px;gap:8px;flex-wrap:wrap">
          ${enabled.map(s => `<button class="sm ghost" onclick="reassignRec(${r.id},${s.id})">Record on ${esc(s.label)}</button>`).join('')}
          <button class="sm" onclick="bumpRec(${r.id})">Bump to Can't-Miss</button>
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
PAGES.settings = {
  title: 'Settings',
  async render(el) {
    const s = await api.get('/api/settings');
    el.innerHTML = `<div class="card" style="max-width:760px"><div class="fields" style="display:grid;grid-template-columns:1fr 1fr;gap:14px">
      ${Object.entries(s).map(([k, v]) => `<label class="field">${k}<input data-k="${esc(k)}" value="${esc(v)}"/></label>`).join('')}
    </div><div class="row" style="margin-top:18px"><button onclick="saveSettings()">Save settings</button><span id="setMsg" class="muted"></span></div></div>`;
  },
};

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

async function startRec(id) { const r = await api.post(`/api/recordings/${id}/start`); if (r.error) toast(r.error, 'err'); else toast(r.started ? 'Starting…' : 'Already running', 'ok'); render(); }
async function stopRec(id) { const r = await api.post(`/api/recordings/${id}/stop`); toast(r.cancelled ? 'Cancelled' : r.stopping ? 'Stopping…' : 'No change', r.error ? 'err' : 'ok'); render(); }
async function delRec(id) { if (!confirm('Delete this recording?')) return; await api.del(`/api/recordings/${id}`); toast('Deleted'); render(); }
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
      <label class="field" style="flex-direction:row;align-items:center;gap:8px"><input id="sEnabled" type="checkbox" ${edit ? ck(x.enabled) : 'checked'} style="width:auto"/> Enabled</label>
    </div>
    <div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitSource(${edit ? x.id : 'null'})">${edit ? 'Save' : 'Add'} source</button></div>`, 'min(620px,94vw)');
}
async function submitSource(id) {
  const body = {
    label: $('#sLabel').value, type: $('#sType').value, protocol: $('#sProto').value,
    host: $('#sHost').value, port: parseInt($('#sPort').value) || 0, maxStreams: parseInt($('#sMax').value) || 1,
    username: $('#sUser').value, password: $('#sPass').value,
    epgUrl: $('#sEpg').value, epgOverride: $('#sEpgOv').checked, enabled: $('#sEnabled').checked,
  };
  closeModals();
  if (id == null) { const r = await api.post('/api/sources', body); toast(r.error ? r.error : `Source added (#${r.id})`, r.error ? 'err' : 'ok'); }
  else { await api.put('/api/sources/' + id, body); toast('Source saved', 'ok'); }
  render();
}
async function deleteSource(id, label) {
  if (!confirm(`Delete source “${label}”?\nThis removes its channels and EPG (recordings are kept).`)) return;
  const r = await api.del('/api/sources/' + id);
  if (r.error) toast(r.error, 'err'); else { toast('Source deleted', 'ok'); render(); }
}
async function saveSettings() {
  const vals = {};
  document.querySelectorAll('#view [data-k]').forEach(i => vals[i.dataset.k] = i.value);
  await api.put('/api/settings', vals);
  $('#setMsg').textContent = 'saved ✓';
  setTimeout(() => { const m = $('#setMsg'); if (m) m.textContent = ''; }, 1800);
}

// ---- leagues (TheSportsDB pickers) / events / mappings actions ----
async function openLeagueModal(id) {
  const x = id != null ? (window._leagues || []).find(l => l.id === id) : null;
  const edit = !!x;
  modal(`<h2>${edit ? 'Edit' : 'Add'} league</h2><div class="fields">
    <label class="field">Sport<select id="lSport"><option>Loading…</option></select></label>
    <label class="field">League <span class="muted">(search)</span><input id="lLeagueQ" placeholder="e.g. AFL, supercars, premier league…"/><select id="lLeague" size="6"><option>Pick a sport first…</option></select></label>
    <label class="field">…or paste a TheSportsDB league id <span class="muted">(for anything not listed)</span><input id="lManualId" value="${esc(x?.externalLeagueId || '')}" placeholder="e.g. 4370"/></label>
    <label class="field">Auto-schedule horizon (days)<input id="lHorizon" type="number" value="${x?.scheduleHorizonDays || 14}"/></label>
    <label class="field">Calendar colour<input type="hidden" id="lColor" value="${esc(x?.color || '')}"/>
      <div class="swatches" id="lSwatches">${LEAGUE_COLORS.map(c => `<span class="swatch${(x?.color || '').toLowerCase() === c ? ' sel' : ''}" data-c="${c}" style="background:${c}" title="${c}"></span>`).join('')}</div></label>
    <label class="field" style="flex-direction:row;align-items:center;gap:8px"><input id="lMon" type="checkbox" ${(!x || x.monitored) ? 'checked' : ''} style="width:auto"/> Monitored — auto-record this league's events</label>
    </div><div class="foot"><button class="ghost" onclick="closeModals()">Cancel</button><button onclick="submitLeague(${edit ? x.id : 'null'})">${edit ? 'Save' : 'Add'} league</button></div>`, 'min(560px,94vw)');

  $('#lSwatches').querySelectorAll('.swatch').forEach(sw => sw.addEventListener('click', () => {
    $('#lSwatches').querySelectorAll('.swatch').forEach(o => o.classList.remove('sel'));
    sw.classList.add('sel'); $('#lColor').value = sw.dataset.c;
  }));

  let leagues = [];
  const renderLeagues = () => {
    const q = $('#lLeagueQ').value;
    const f = q ? leagues.filter(l => tokensMatch(`${l.name} ${l.alternate || ''} ${l.country || ''}`, q)) : leagues;
    $('#lLeague').innerHTML = f.slice(0, 500).map(l => `<option value="${esc(l.id)}" data-name="${esc(l.name)}" data-sport="${esc(l.sport)}" ${x?.externalLeagueId === l.id ? 'selected' : ''}>${esc(l.name)}${l.country ? ` (${esc(l.country)})` : ''}</option>`).join('') || '<option value="">(no leagues)</option>';
  };
  const loadLeagues = async () => {
    $('#lLeague').innerHTML = '<option>Loading…</option>';
    leagues = await api.get('/api/tsdb/leagues?sport=' + encodeURIComponent($('#lSport').value));
    renderLeagues();
  };
  const sports = await api.get('/api/tsdb/sports');
  $('#lSport').innerHTML = sports.map(s => `<option ${x?.sport === s.name ? 'selected' : ''}>${esc(s.name)}</option>`).join('') || '<option>(TheSportsDB unavailable)</option>';
  $('#lSport').onchange = loadLeagues;
  let lt; $('#lLeagueQ').oninput = () => { clearTimeout(lt); lt = setTimeout(renderLeagues, 150); };
  await loadLeagues();
}
async function submitLeague(id) {
  const opt = $('#lLeague').selectedOptions[0];
  const manual = $('#lManualId').value.trim();
  const externalLeagueId = manual || $('#lLeague').value;
  if (!externalLeagueId) return toast('Pick a league or paste a TheSportsDB id', 'err');
  // For a manually-pasted id, let the server fill name/sport from TheSportsDB (lookupleague by id works on the free key).
  const body = {
    externalLeagueId,
    name: manual ? undefined : opt?.dataset.name,
    sport: manual ? undefined : (opt?.dataset.sport || $('#lSport').value),
    scheduleHorizonDays: parseInt($('#lHorizon').value) || 14, monitored: $('#lMon').checked, color: $('#lColor').value || '',
  };
  closeModals();
  if (id == null) { const r = await api.post('/api/leagues', body); toast(r.error ? r.error : 'League added', r.error ? 'err' : 'ok'); }
  else { await api.put('/api/leagues/' + id, body); toast('League saved', 'ok'); }
  render();
}
async function deleteLeague(id, name) { if (!confirm(`Delete league “${name}”? Removes its events & mappings.`)) return; await api.del('/api/leagues/' + id); toast('League deleted', 'ok'); render(); }
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
async function deleteMapping(id) { await api.del('/api/mappings/' + id); toast('Mapping removed'); render(); }

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
async function monitorEvent(id, mon) { await api.put('/api/events/' + id + '/monitor', { monitored: mon }); toast(mon ? 'Monitoring' : 'Unmonitored'); render(); }
async function resolvePreview(id) { const r = await api.get('/api/events/' + id + '/resolve'); if (r.ok) toast(`Resolves to: ${r.primary.channelName} (score ${Math.round(r.primary.score)})`, 'ok'); else toast(`Cannot resolve: ${r.reason}`, 'err'); }

// =========================================================================
// router
// =========================================================================
async function render() {
  closeModals(); // navigating away must tear down any open modal — esp. a live preview holding the stream slot
  const id = (location.hash.replace(/^#\//, '') || 'dashboard').split('?')[0];
  const page = PAGES[id] || PAGES.dashboard;
  document.querySelectorAll('.nav-item').forEach(a => a.classList.toggle('active', a.dataset.route === id));
  $('#pageTitle').textContent = page.title;
  $('#pageActions').innerHTML = page.actions ? page.actions() : '';
  setLive(null);
  const view = $('#view');
  view.innerHTML = '<div class="loading">Loading…</div>';
  try { await page.render(view); }
  catch (e) { view.innerHTML = emptyBox('Failed to load this page: ' + e.message); }
}

window.addEventListener('hashchange', render);
window.addEventListener('keydown', e => { if (e.key === 'Escape') closeModals(); }); // Esc closes modals + stops preview
window.render = render; window.openTestModal = openTestModal; window.submitTest = submitTest;
window.openScheduleModal = openScheduleModal; window.submitSchedule = submitSchedule; window.scheduleFor = scheduleFor; window.scheduleFromGuide = scheduleFromGuide;
window.openPreview = openPreview; window.stopRec = stopRec; window.startRec = startRec; window.delRec = delRec;
window.ingest = ingest; window.doIngest = doIngest; window.saveSettings = saveSettings; window.closeModals = closeModals;
window.syncEpg = syncEpg; window.doSyncEpg = doSyncEpg; window.openSourceModal = openSourceModal; window.submitSource = submitSource; window.deleteSource = deleteSource;
window.openLeagueModal = openLeagueModal; window.submitLeague = submitLeague; window.deleteLeague = deleteLeague; window.syncLeague = syncLeague;
window.openMapModal = openMapModal; window.submitMap = submitMap; window.deleteMapping = deleteMapping;
window.openEventModal = openEventModal; window.submitEvent = submitEvent; window.monitorEvent = monitorEvent; window.resolvePreview = resolvePreview; window.openCalEvent = openCalEvent;

buildNav();
if (!location.hash) location.hash = '#/dashboard';
render();
pollHealth();
setInterval(pollHealth, 5000);
connectSSE();
