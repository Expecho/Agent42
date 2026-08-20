/* Agent 42 — offline read cache — IndexedDB kv mirror (ADR 0032). Split out of app.js (ADR 0041). */

// Agents, conversations and previously-opened chat histories are mirrored into IndexedDB so the
// installed PWA can render the user's data offline. This is a client-side read cache ONLY — the
// service worker still never touches /api (ADR 0002); the server stays the source of truth and
// every successful fetch overwrites the mirror. Best-effort throughout: any IndexedDB error
// resolves to a no-op (private mode, quota) and never blocks a load. A single "kv" store keyed by
// "agents" / "conversations" / "messages:<id>".
const CACHE_DB = "agent42-cache";
const CACHE_STORE = "kv";

function cacheDb() {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(CACHE_DB, 1);
    open.onupgradeneeded = () => open.result.createObjectStore(CACHE_STORE);
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(open.error);
  });
}

export function cacheGet(key) {
  return new Promise((resolve) => {
    cacheDb().then((db) => {
      const get = db.transaction(CACHE_STORE, "readonly").objectStore(CACHE_STORE).get(key);
      get.onsuccess = () => resolve(get.result ?? null);
      get.onerror = () => resolve(null);
    }).catch(() => resolve(null));
  });
}

export function cachePut(key, value) {
  return new Promise((resolve) => {
    cacheDb().then((db) => {
      const tx = db.transaction(CACHE_STORE, "readwrite");
      tx.objectStore(CACHE_STORE).put(value, key);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
    }).catch(() => resolve());
  });
}

export function cacheDelete(key) {
  return new Promise((resolve) => {
    cacheDb().then((db) => {
      const tx = db.transaction(CACHE_STORE, "readwrite");
      tx.objectStore(CACHE_STORE).delete(key);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
    }).catch(() => resolve());
  });
}
