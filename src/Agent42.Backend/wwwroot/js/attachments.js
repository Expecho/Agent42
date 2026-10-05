/* Agent 42 — compose-time attachments (ADR 0004/0007). Split out of app.js (ADR 0041). */

import { ephemeral } from "./agents.js";
import { activeAgentKind, activeId } from "./conversations.js";
import { attachBtn, attachmentPreview, fileInput } from "./dom.js";
import { userDefaultEmbeddingModel } from "./providers.js";
import { streaming } from "./send.js";
import { attachmentUrl } from "./thread.js";

// Images uploaded for the turn being composed: {id, mediaType, fileName} from the upload
// endpoint. Sent as attachmentIds on the next send, then cleared. Reset on conversation switch.
export let pendingAttachments = [];

// Documents uploaded to this conversation: {kind, fileName, chunkCount}. Display-only — a
// document is ingest-only (indexed server-side for automatic retrieval, ADR 0007/0008), so it
// is NEVER sent back as an attachmentId. The chip is a "just indexed" indicator; a chunkCount of
// 0 means no text could be extracted (e.g. a scanned PDF), so it renders as a warning instead —
// the server did NOT register that document with the grain, so the agent stays unaware of it.
let pendingDocuments = [];

// Non-OK uploads for the turn being composed: {fileName, message}. Rendered as dismissible error
// chips so a rejected/failed upload is surfaced truthfully instead of silently swallowed. Reset
// on conversation switch, like the other pending arrays.
let pendingNotices = [];
// Uploads each picked file to the current conversation. The server branches on media type: an
// image comes back as {id,...} and is sent as an attachmentId on the next turn; a document comes
// back as {kind:"document",...}, is indexed for retrieval server-side, and is shown as a chip but
// never sent back (ADR 0007). A non-OK response (unsupported type, oversize, or a server error)
// is surfaced as a dismissible error chip rather than silently dropped.
export async function handleFiles(files) {
  if (streaming) return;
  // Pin the results to the conversation the upload was started in: the fetch stores bytes under
  // uploadId, so a mid-upload conversation switch must NOT apply the chip/attachment to the new
  // one. After every await we bail via break (not return) so the fileInput reset below still runs
  // and the same file can be re-picked.
  const uploadId = activeId;
  for (const file of files) {
    // Incognito document gate (ADR 0034): RAG ingestion writes durable chunks to the Cosmos vector
    // store, which would outlive an ephemeral chat — so block documents while incognito. Images are
    // fine (session-only ephemerality). The server does not enforce this, so the gate lives here.
    if (ephemeral && !file.type.startsWith("image/")) {
      pendingNotices.push({
        fileName: file.name,
        message: "Documents can't be attached in an incognito chat — they'd be indexed and stored. Turn off incognito to attach documents.",
      });
      renderAttachmentPreview();
      continue;
    }
    // Copilot document gate: a copilot agent's chat has no document retrieval, so a document would be indexed for
    // nothing. The server refuses it too (before ingesting); this says why without the round trip. Images still
    // upload, and the server withholds them from the model with its usual notice.
    if (activeAgentKind === "copilot" && !file.type.startsWith("image/")) {
      pendingNotices.push({
        fileName: file.name,
        message: "A copilot agent's chat doesn't support documents.",
      });
      renderAttachmentPreview();
      continue;
    }
    // Document-attach gate (ADR 0007/0018): a document needs an embedding model to be indexed for RAG, so
    // block the attempt when none is configured — images never need embeddings and are always allowed. The
    // server also rejects it (defense-in-depth); this just avoids a pointless upload + a cryptic error.
    // Note: loadProviders() populates userDefaultEmbeddingModel fire-and-forget at init, so in the brief
    // window before it resolves a configured user could see a transient false-negative here — the server
    // would still accept the upload; the gate self-corrects once /api/providers returns.
    if (!userDefaultEmbeddingModel && !file.type.startsWith("image/")) {
      pendingNotices.push({
        fileName: file.name,
        message: "Add a model marked as an embedding model to a provider and pick it as the default embedding model in Preferences to attach documents.",
      });
      renderAttachmentPreview();
      continue;
    }
    try {
      const body = new FormData();
      body.append("file", file, file.name);
      const res = await fetch(attachmentsUrl(uploadId), { method: "POST", body });
      if (activeId !== uploadId) break; // user moved on — discard this result
      if (!res.ok) {
        const message = (await res.text()).trim() || "Upload failed.";
        if (activeId !== uploadId) break;
        pendingNotices.push({ fileName: file.name, message });
        renderAttachmentPreview();
        continue;
      }
      const meta = await res.json();
      if (activeId !== uploadId) break;
      if (meta.kind === "document") {
        pendingDocuments.push(meta);
      } else {
        pendingAttachments.push(meta);
      }
      renderAttachmentPreview();
    } catch { /* skip this file; the others still upload */ }
  }
  fileInput.value = ""; // let the same file be picked again after a remove
}

const attachmentsUrl = (convId) =>
  `/api/conversations/${encodeURIComponent(convId)}/attachments`;

function renderAttachmentPreview() {
  attachmentPreview.innerHTML = "";
  attachmentPreview.hidden =
    pendingAttachments.length === 0 && pendingDocuments.length === 0 && pendingNotices.length === 0;
  pendingNotices.forEach((notice) => {
    // A rejected/failed upload — surface it as a dismissible error chip (the server never stored
    // anything, so dismissing only clears the notice). Reuses the doc-chip shape; the --error
    // variant paints it red (styles.css).
    const chip = document.createElement("div");
    chip.className = "attach-chip attach-chip--doc attach-chip--error";
    chip.title = notice.message;

    const label = document.createElement("span");
    label.className = "attach-chip__doc";
    label.textContent = `⚠️ ${notice.fileName} — upload failed`;

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "attach-chip__remove";
    remove.setAttribute("aria-label", `Dismiss ${notice.fileName}`);
    remove.textContent = "×";
    remove.addEventListener("click", () => {
      pendingNotices = pendingNotices.filter((n) => n !== notice);
      renderAttachmentPreview();
    });

    chip.append(label, remove);
    attachmentPreview.appendChild(chip);
  });
  pendingDocuments.forEach((doc) => {
    // chunkCount 0 = no extractable text, so the server never registered it with the grain and
    // the agent won't see it. Warn clearly rather than showing a "just indexed" chip that invites
    // a question the agent can't answer.
    const indexed = doc.chunkCount > 0;
    const chip = document.createElement("div");
    chip.className = indexed
      ? "attach-chip attach-chip--doc"
      : "attach-chip attach-chip--doc attach-chip--warn";
    chip.title = indexed
      ? `${doc.fileName} — ${doc.chunkCount} passage(s) indexed. Ask a question about it.`
      : `${doc.fileName} — no readable text found (a scanned image?). It was NOT indexed, so the assistant can't answer questions about it.`;

    const label = document.createElement("span");
    label.className = "attach-chip__doc";
    label.textContent = indexed ? `📄 ${doc.fileName}` : `⚠️ ${doc.fileName} — not indexed`;

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "attach-chip__remove";
    remove.setAttribute("aria-label", `Dismiss ${doc.fileName}`);
    remove.textContent = "×";
    // Display-only: dismissing the chip doesn't un-index the document (that happens when the
    // whole conversation is deleted).
    remove.addEventListener("click", () => {
      pendingDocuments = pendingDocuments.filter((d) => d !== doc);
      renderAttachmentPreview();
    });

    chip.append(label, remove);
    attachmentPreview.appendChild(chip);
  });
  pendingAttachments.forEach((att) => {
    const chip = document.createElement("div");
    chip.className = "attach-chip";

    const img = document.createElement("img");
    img.className = "attach-chip__thumb";
    img.alt = att.fileName || "attachment";
    img.src = attachmentUrl(activeId, att.id);

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "attach-chip__remove";
    remove.setAttribute("aria-label", `Remove ${att.fileName || "attachment"}`);
    remove.textContent = "×";
    remove.addEventListener("click", () => {
      pendingAttachments = pendingAttachments.filter((a) => a.id !== att.id);
      renderAttachmentPreview();
    });

    chip.append(img, remove);
    attachmentPreview.appendChild(chip);
  });
}

export function clearPendingAttachments() {
  pendingAttachments = [];
  pendingDocuments = [];
  pendingNotices = [];
  renderAttachmentPreview();
}

export function wireAttachments() {
  attachBtn.addEventListener("click", () => fileInput.click());
  fileInput.addEventListener("change", () => handleFiles([...fileInput.files]));
}
