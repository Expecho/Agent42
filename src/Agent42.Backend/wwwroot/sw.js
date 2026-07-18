/* Agent 42 PWA service worker — installability + safe static caching only.
   Never touches /api (auth-bearing, streaming) and is network-first for
   navigations so the Entra Easy Auth redirect is never masked (see ADR 0002 and
   the gotchas entry on SW navigation caching). */
const CACHE = "agent42-shell-v7";
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

/* ————— Web Share Target (Android/Chromium only) —————
   The manifest's share_target posts multipart/form-data to /share. We intercept it here — before
   it reaches the network — stash the payload in IndexedDB, and answer with a 303 redirect to a GET
   landing (/?share=1). This is the auth pivot: a POST navigation to a protected path would 302 to
   the Easy Auth login and LOSE the body (ADR 0002); the SW-synthesized POST never leaves the
   client, so Easy Auth never sees it, and the payload survives the login round-trip in IndexedDB
   (origin-scoped). app.js reads the stash on the /?share=1 load. Requires the SW to be active and
   controlling (true after install on Android); if it isn't, the POST reaches the server and fails
   ungracefully — an accepted platform limit, NOT a server /share route (that reintroduces the
   body-loss problem). See the ADR and gotchas entry. */
const SHARE_DB = "agent42-share";
const SHARE_STORE = "pending";
const SHARE_KEY = "payload";

function shareDb() {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(SHARE_DB, 1);
    open.onupgradeneeded = () => open.result.createObjectStore(SHARE_STORE);
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(open.error);
  });
}

function sharePut(record) {
  return shareDb().then((db) => new Promise((resolve, reject) => {
    const tx = db.transaction(SHARE_STORE, "readwrite");
    tx.objectStore(SHARE_STORE).put(record, SHARE_KEY);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  }));
}

async function handleShare(request) {
  const form = await request.formData();
  // File parts are Files (Blobs with a name); store the Blob directly — IndexedDB persists it and
  // app.js rebuilds a File from it. Text-only shares carry no "file" part.
  const files = form.getAll("file")
    .filter((f) => f instanceof Blob && f.name)
    .map((f) => ({ name: f.name, type: f.type, blob: f }));
  await sharePut({
    title: form.get("title") || "",
    text: form.get("text") || "",
    url: form.get("url") || "",
    files,
  });
  return Response.redirect("/?share=1", 303);
}

self.addEventListener("fetch", (event) => {
  const req = event.request;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return; // never touch cross-origin
  if (url.pathname.startsWith("/api")) return; // never cache chat/auth API — must precede any respondWith
  // Share branch sits AFTER the /api guard (keeps the ADR-0002 invariant / the position test green)
  // and BEFORE the non-GET early-return below (a share is a POST).
  if (req.method === "POST" && url.pathname === "/share") { event.respondWith(handleShare(req)); return; }
  if (req.method !== "GET") return;

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
