'use strict';
// DVarr service worker — makes the app installable + loads the shell instantly (and offline) over the VPN.
// Strategy: stale-while-revalidate for the static app shell; the /api/* surface (live data, SSE, preview streams)
// is NEVER cached or intercepted so the UI always shows real-time state. Bump VERSION on each release to purge old shells.
const VERSION = 'dvarr-shell-v1.37.0';
const SHELL = [
  '/', '/index.html', '/styles.css',
  '/js/app.js', '/js/mpegts.js', '/js/hls.js',
  '/manifest.webmanifest',
  '/icons/icon-192.png', '/icons/icon-512.png', '/icons/icon-180.png',
  '/dvarr-logo.png',
];

self.addEventListener('install', e => {
  e.waitUntil(
    caches.open(VERSION)
      .then(c => Promise.allSettled(SHELL.map(u => c.add(u)))) // tolerate a missing asset rather than failing the whole install
      .then(() => self.skipWaiting())
      .catch(() => self.skipWaiting())
  );
});

self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== VERSION).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', e => {
  const req = e.request;
  if (req.method !== 'GET') return;                       // never touch writes
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;        // 3rd-party → straight to network
  if (url.pathname.startsWith('/api/')) return;           // live data / SSE / preview → real-time only, never cached
  // App shell: serve from cache immediately, refresh the cached copy in the background.
  e.respondWith(
    caches.open(VERSION).then(cache => cache.match(req).then(hit => {
      const net = fetch(req).then(res => { if (res && res.ok && res.type === 'basic') cache.put(req, res.clone()); return res; }).catch(() => hit);
      return hit || net;
    }))
  );
});
