const LABELS = {
  connected: "Connected to Agent42",
  connecting: "Connecting…",
  retrying: "Not connected, retrying",
  "key-rejected": "Bridge key rejected",
  replaced: "Another bridge took over this key",
  stopped: "Stopped",
  unconfigured: "Not set up yet",
};

async function render() {
  const { status } = await chrome.storage.session.get("status");
  const { yolo } = await chrome.storage.local.get({ yolo: false });
  document.getElementById("status").textContent = LABELS[status?.status] ?? "Starting…";
  document.getElementById("detail").textContent = status?.detail ?? "";
  document.getElementById("yolo").hidden = !yolo;
}

document.getElementById("reconnect").addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ type: "reconnect" });
  setTimeout(render, 500);
});
document.getElementById("options").addEventListener("click", () => chrome.runtime.openOptionsPage());
chrome.storage.onChanged.addListener(render);
render();
