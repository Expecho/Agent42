// The approval window. The request text is attacker-influenced (the model writes the arguments after reading
// web pages); the gate already sanitized it, and it is only ever written with textContent, never as markup.

const id = new URLSearchParams(location.search).get("id");
const buttons = ["deny", "once", "always"].map((name) => document.getElementById(name));

const request = await chrome.runtime.sendMessage({ type: "approval-request", id });
if (!request) {
  document.getElementById("gone").hidden = false;
  for (const b of buttons) b.disabled = true;
} else {
  document.getElementById("tool").textContent = request.tool;
  document.getElementById("target").textContent = request.target || "No tab (opens or lists tabs)";
  document.getElementById("args").textContent = request.args;
  for (const button of buttons) {
    button.addEventListener("click", async () => {
      for (const b of buttons) b.disabled = true;
      await chrome.runtime.sendMessage({ type: "approval-answer", id, answer: button.id });
      window.close();
    });
  }
}
