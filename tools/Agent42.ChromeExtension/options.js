import { defaultBlockedSites, normalizeServer } from "./lib/config.js";

const $ = (id) => document.getElementById(id);
const stored = await chrome.storage.local.get({ server: "", key: "", yolo: false, blocked: null });

$("server").value = stored.server;
$("key").value = stored.key;
$("yolo").checked = stored.yolo;
// Until the user edits the list, its default follows the address being entered: the Agent42 site itself is blocked,
// whichever Agent42 that is.
let blockedEdited = stored.blocked !== null;
const syncDefaultBlocked = () => {
  if (!blockedEdited) $("blocked").value = defaultBlockedSites($("server").value || "https://chat.agent42.nl");
};
$("blocked").value = stored.blocked ?? "";
syncDefaultBlocked();
$("blocked").addEventListener("input", () => (blockedEdited = true));
$("server").addEventListener("input", syncDefaultBlocked);

$("save").addEventListener("click", async () => {
  const server = normalizeServer($("server").value);
  if (!server) {
    $("saved").textContent = "The address must be https:// (or http:// on localhost).";
    return;
  }
  syncDefaultBlocked();
  const key = $("key").value.trim();
  const blocked = $("blocked").value;
  await chrome.storage.local.set({ server, key, yolo: $("yolo").checked, blocked });
  $("server").value = server;
  $("saved").textContent = "Saved. The bridge reconnects with these settings.";
});
