/* Agent 42 PWA service worker — installability + safe static caching only.
   Never touches /api (auth-bearing, streaming) and is network-first for
   navigations so the Entra Easy Auth redirect is never masked (see ADR 0002 and
   the gotchas entry on SW navigation caching). */
const CACHE = "agent42-shell-v6";
const SHELL = ["/", "/index.html", "/styles.css", "/app.js",
  "/lib/marked.min.js", "/lib/purify.min.js", "/manifest.webmanifest",
  "/icons/icon-192.png", "/icons/icon-512.png", "/icons/icon-maskable-512.png"];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;
  if (url.pathname.startsWith("/api")) return; // never cache chat/auth API

  if (req.mode === "navigate") {
    // Network-first: lets the Easy Auth 302 through; cached shell only when offline.
    event.respondWith(fetch(req).catch(() => caches.match("/index.html")));
    return;
  }

  // Stale-while-revalidate for static assets.
  event.respondWith(
    caches.match(req).then((cached) => {
      const network = fetch(req).then((res) => {
        if (res.ok) { const copy = res.clone(); caches.open(CACHE).then((c) => c.put(req, copy)); }
        return res;
      }).catch(() => cached);
      return cached || network;
    }));
});
