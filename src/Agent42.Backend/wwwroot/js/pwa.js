/* Agent 42 — PWA — service worker registration. Split out of app.js (ADR 0041). */

export function wirePwa() {
  if ("serviceWorker" in navigator) {
    window.addEventListener("load", () => {
      navigator.serviceWorker.register("/sw.js").catch((err) =>
        console.warn("Service worker registration failed", err));
    });
  }
}
