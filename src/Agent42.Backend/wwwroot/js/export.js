/* Agent 42 — "Export all conversations" (ADR 0091). One ZIP per server part, never one request for everything:
   each conversation costs a grain activation, so the server pages the export and this loop walks the parts. */

import { exportAllBtn, exportAllStatus } from "./dom.js";

// Saves one part through a temporary <a download>. The object URL is revoked on a later task, not right after the
// click: Safari has been seen to cancel a download whose blob URL is revoked synchronously.
function saveBlob(blob, fileName) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// Walks the parts by keyset: each response names the last id it held in X-Export-Next, and the next request asks
// for the ids after it, so a chat added or deleted mid-walk never shifts a part. A failed part stops the walk with an
// error line — the parts already saved stay saved.
async function exportAll() {
  exportAllBtn.disabled = true;
  const date = new Date().toISOString().slice(0, 10);
  let next = null;
  try {
    do {
      const res = await fetch("/api/export" + (next ? "?after=" + encodeURIComponent(next) : ""));
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const part = res.headers.get("X-Export-Part");
      const parts = res.headers.get("X-Export-Parts");
      exportAllStatus.textContent = `Part ${part} of ${parts}…`;
      saveBlob(await res.blob(), `agent42-export-${date}-part-${part}-of-${parts}.zip`);
      next = res.headers.get("X-Export-Next");
    } while (next);
    exportAllStatus.textContent = "Done";
  } catch {
    exportAllStatus.textContent = "Export failed — try again";
  } finally {
    exportAllBtn.disabled = false;
  }
}

export function wireExport() {
  exportAllBtn.addEventListener("click", exportAll);
}
