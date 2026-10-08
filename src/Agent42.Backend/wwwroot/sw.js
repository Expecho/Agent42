/* Agent 42 PWA service worker — installability + safe static caching only.
   Never touches /api (auth-bearing, streaming) and is network-first for
   navigations so the Entra Easy Auth redirect is never masked (see ADR 0002 and
   the gotchas entry on SW navigation caching). */
const CACHE = "agent42-shell-v47";
const SHELL = ["/", "/index.html", "/styles.css", "/js/agents.js", "/js/artifacts.js", "/js/attachments.js",
  "/js/cache.js", "/js/composer.js", "/js/confighealth.js", "/js/conversations.js", "/js/dom.js",
  "/js/drawer.js", "/js/format.js", "/js/groups.js", "/js/location.js", "/js/main.js", "/js/mcp.js",
  "/js/models.js", "/js/profile.js", "/js/providers.js", "/js/push.js", "/js/pwa.js",
  "/js/research.js", "/js/search.js", "/js/send.js", "/js/sharelinks.js", "/js/sharetarget.js", "/js/sidebar.js", "/js/skills.js",
  "/js/speech.js",
  "/js/tabs.js", "/js/theme.js", "/js/thread.js", "/js/tools.js", "/js/usage.js", "/js/voice.js",
  "/lib/marked.min.js", "/lib/purify.min.js",
  // Rich rendering (ADR 0083): only the EAGER part. mermaid/KaTeX/Chart.js are lazy and reach the cache at
  // runtime through stale-while-revalidate; a 5 MB mermaid in this all-or-nothing addAll would slow install
  // and add a failure point.
  "/shared/lib/richtext.js", "/shared/lib/artifactpane.js", "/shared/lib/highlight/highlight.min.js",
  "/shared/lib/highlight/github.min.css", "/shared/lib/highlight/github-dark.min.css",
  "/manifest.webmanifest", "/icons/icon-192.png",
  "/icons/icon-512.png", "/icons/icon-maskable-512.png"];

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
   (origin-scoped). js/sharetarget.js reads the stash on the /?share=1 load. Requires the SW to be
   active and controlling (true after install on Android); if it isn't, the POST reaches the server
   and fails ungracefully — an accepted platform limit, NOT a server /share route (that reintroduces the
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
  // js/sharetarget.js rebuilds a File from it. Text-only shares carry no "file" part.
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
  // Same rule, same reason, for the proxied SearXNG UI (ADR 0059): its subresources carry the Easy Auth
  // cookie and per-user SearXNG preference cookies, so they must never land in the SHARED shell cache.
  // Also keeps the navigate branch below from painting /index.html — the chat app — when a /searxng/
  // navigation fails offline. Must precede any respondWith, exactly like the /api guard.
  if (url.pathname.startsWith("/searxng")) return;
  // Same rule again for the Orleans dashboard (ADR 0062). It is server-rendered by the package, not part
  // of the cached shell, and its 3.5 MB bundle has no business in a shared cache. It also must not be
  // painted over by the navigate branch: the gate answers a non-owner with 404, and falling back to
  // /index.html would show the chat app instead — a cached shell silently contradicting the gate.
  // Must precede any respondWith, exactly like the /api and /searxng guards.
  if (url.pathname.startsWith("/dashboard")) return;
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

/* ————— Web Push notifications (ADR 0028) —————
   A push arrives when an agent replies on an ASYNC path (a fired reminder or an agent-run) while the
   user is away. The payload is {title, body, conversationId}; the title is the agent's name.

   We ALWAYS show the notification — deliberately NO "suppress if a client is focused" trick. Chromium's
   userVisibleOnly:true contract requires a shown notification for every received push; skipping one burns
   push budget and, once exhausted, Chrome shows its own generic "site updated in the background" notice and
   can REVOKE the subscription — and that penalty lands in exactly the target case (a background reply
   completing while the PWA happens to be open on this device). Away-ness is carried by "the server only
   pushes on the two async paths" (AgentGrain), not by the service worker. */
self.addEventListener("push", (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = {}; }
  const title = data.title || "Agent 42";
  event.waitUntil(
    self.registration.showNotification(title, {
      body: data.body || "",
      icon: "/icons/icon-192.png",
      badge: "/icons/icon-192.png",
      data: { conversationId: data.conversationId || "" },
    }));
});

/* Tapping the notification focuses an existing app window and tells js/push.js which conversation to
   open (its `message` handler calls selectConversation), or opens a fresh window at "/" when none is open. */
self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const conversationId = (event.notification.data && event.notification.data.conversationId) || "";
  event.waitUntil((async () => {
    const clients = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of clients) {
      if ("focus" in client) {
        await client.focus();
        client.postMessage({ type: "open-conversation", conversationId });
        return;
      }
    }
    if (self.clients.openWindow) {
      await self.clients.openWindow("/");
    }
  })());
});
