/* Agent 42 — composer textarea. Split out of app.js (ADR 0041). */

import { form, input } from "./dom.js";

export function autosize() {
  input.style.height = "auto";
  input.style.height = Math.min(input.scrollHeight, 180) + "px";
}

export function wireComposer() {
  input.addEventListener("input", autosize);

  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      form.requestSubmit();
    }
  });
}
