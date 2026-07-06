// Harbora service worker — caches the static app shell for a fast, offline-tolerant load.
// API and auth requests always go to the network; only build assets + the offline page are cached.
const CACHE = 'harbora-shell-v1';
const SHELL = ['/offline.html', '/css/fallback.css', '/manifest.webmanifest'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;

  const url = new URL(req.url);
  // Never cache API, auth, hubs — always live.
  if (url.pathname.startsWith('/api') || url.pathname.startsWith('/account') || url.pathname.startsWith('/hubs')) return;

  // Cache-first for build assets; network-first with offline fallback for pages.
  if (url.pathname.startsWith('/build') || url.pathname.startsWith('/css') || url.pathname.startsWith('/icons')) {
    event.respondWith(caches.match(req).then((hit) => hit || fetch(req).then((res) => {
      const copy = res.clone();
      caches.open(CACHE).then((c) => c.put(req, copy));
      return res;
    })));
    return;
  }

  event.respondWith(fetch(req).catch(() => caches.match('/offline.html')));
});
