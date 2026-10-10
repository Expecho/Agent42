/* Agent 42 — MCP elicitation (ADR 0095): a remote MCP server asks the user for input while one of its tools runs.
   The server streams an `mcpElicitation` line, send.js hands it to clienttools.js as an `mcp_elicitation` call, and
   this module shows it and returns {action, content?}. The answer goes back on the client-tool result endpoint.

   Everything the server sends is untrusted and is set with textContent, never as markup. URL mode never opens or
   prefetches the URL by itself: it opens only when the user clicks Open, with noopener and noreferrer. */

import {
  mcpElicitationDeclineBtn, mcpElicitationDialog, mcpElicitationFields, mcpElicitationHost, mcpElicitationMessage,
  mcpElicitationServer, mcpElicitationSubmitBtn, mcpElicitationUrl, mcpElicitationUrlBox,
} from "./dom.js";

// One request on screen at a time; later ones wait their turn.
let queue = Promise.resolve();
// Settles the request currently on screen, or null.
let settle = null;
// Builds the accept answer from the dialog's state when Submit/Open is clicked; null when it cannot.
let acceptAnswer = null;
// That accept answer, built at click time and read when the dialog closes.
let pendingAccept = null;

const FORMAT_INPUT_TYPES = { email: "email", uri: "url", date: "date", "date-time": "datetime-local" };

// Shows one elicitation and resolves with the answer. Never rejects.
export function showElicitation(input) {
  const ask = () => new Promise((resolve) => {
    if (!mcpElicitationDialog) {
      resolve({ action: "cancel" });
      return;
    }
    settle = resolve;
    render(input);
    // Reset first: Esc closes a dialog WITHOUT setting returnValue, so the previous request's "accept" would
    // otherwise still be there and turn an Esc into an accept.
    mcpElicitationDialog.returnValue = "";
    mcpElicitationDialog.showModal();
  });
  const answer = queue.then(ask);
  queue = answer.catch(() => {});
  return answer;
}

// The turn is over: a request still on screen is cancelled (and its late answer's 404 ignored).
export function closeElicitationDialog() {
  if (mcpElicitationDialog?.open) mcpElicitationDialog.close("stale");
}

function render(input) {
  mcpElicitationServer.textContent = `Requested by ${String(input.server ?? "an MCP server")}`;
  mcpElicitationMessage.textContent = String(input.message ?? "");
  mcpElicitationFields.replaceChildren();
  mcpElicitationUrlBox.hidden = true;
  mcpElicitationSubmitBtn.hidden = false;
  if (input.mode === "url") {
    renderUrl(input);
  } else {
    renderForm(input.schema);
  }
}

function renderUrl(input) {
  const url = safeUrl(input.url);
  if (!url) {
    renderUnsupported();
    return;
  }
  mcpElicitationHost.textContent = url.host;
  mcpElicitationUrl.textContent = url.href;
  mcpElicitationUrlBox.hidden = false;
  mcpElicitationSubmitBtn.textContent = "Open";
  acceptAnswer = () => {
    // The only place the URL is opened: inside the Open click, never on render.
    window.open(url.href, "_blank", "noopener,noreferrer");
    return { action: "accept" };
  };
}

// The server checked the URL already; this is the page's own check, so a bad one is never even offered.
function safeUrl(raw) {
  try {
    const url = new URL(String(raw ?? ""));
    const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
    return url.protocol === "https:" || (url.protocol === "http:" && loopback) ? url : null;
  } catch {
    return null;
  }
}

function renderForm(schema) {
  const properties = schema && typeof schema.properties === "object" ? schema.properties : {};
  const required = new Set(Array.isArray(schema?.required) ? schema.required : []);
  const readers = [];
  for (const [name, definition] of Object.entries(properties)) {
    const field = buildField(name, definition ?? {}, required.has(name));
    if (!field) {
      renderUnsupported();
      return;
    }
    mcpElicitationFields.append(field.element);
    readers.push([name, field.read]);
  }
  mcpElicitationSubmitBtn.textContent = "Submit";
  acceptAnswer = () => {
    for (const control of mcpElicitationFields.querySelectorAll("input, select")) {
      if (!control.checkValidity()) {
        control.reportValidity();
        return null;
      }
    }
    const content = {};
    for (const [name, read] of readers) {
      const value = read();
      if (value !== undefined) content[name] = value;
    }
    return { action: "accept", content };
  };
}

// A request this page cannot render faithfully gets no form at all: only Decline.
function renderUnsupported() {
  mcpElicitationFields.replaceChildren();
  const note = document.createElement("p");
  note.className = "agent-form__note";
  note.textContent = "This request can't be shown.";
  mcpElicitationFields.append(note);
  mcpElicitationUrlBox.hidden = true;
  mcpElicitationSubmitBtn.hidden = true;
  acceptAnswer = null;
}

function buildField(name, definition, isRequired) {
  const label = document.createElement("label");
  label.className = "agent-form__field";
  const caption = document.createElement("span");
  caption.className = "agent-form__label";
  caption.textContent = `${String(definition.title ?? name)}${isRequired ? " *" : ""}`;
  label.append(caption);
  const control = buildControl(definition, isRequired);
  if (!control) return null;
  label.append(control.element);
  if (definition.description) {
    const hint = document.createElement("span");
    hint.className = "agent-form__note";
    hint.textContent = String(definition.description);
    label.append(hint);
  }
  return { element: label, read: control.read };
}

function buildControl(definition, isRequired) {
  const options = enumOptions(definition);
  if (definition.type === "string" && options) return selectControl(options, definition.default, isRequired);
  if (definition.type === "array") {
    const items = enumOptions(definition.items ?? {});
    return items ? multiSelectControl(items, definition.default) : null;
  }
  if (definition.type === "boolean") return checkboxControl(definition.default);
  if (definition.type === "number" || definition.type === "integer") return numberControl(definition, isRequired);
  if (definition.type === "string") return textControl(definition, isRequired);
  return null;
}

// [{value, title}] for untitled "enum", legacy "enum" + "enumNames", and titled "oneOf"/"anyOf"; null otherwise.
function enumOptions(definition) {
  if (Array.isArray(definition.enum)) {
    const names = Array.isArray(definition.enumNames) ? definition.enumNames : [];
    return definition.enum.map((value, i) => ({ value: String(value), title: String(names[i] ?? value) }));
  }
  const titled = Array.isArray(definition.oneOf) ? definition.oneOf : Array.isArray(definition.anyOf) ? definition.anyOf : null;
  return titled ? titled.map((o) => ({ value: String(o?.const ?? ""), title: String(o?.title ?? o?.const ?? "") })) : null;
}

function selectControl(options, fallback, isRequired) {
  const select = document.createElement("select");
  select.className = "agent-form__input";
  select.required = isRequired;
  if (!isRequired) select.append(new Option("", ""));
  for (const option of options) select.append(new Option(option.title, option.value, false, option.value === fallback));
  return { element: select, read: () => (select.value === "" ? undefined : select.value) };
}

function multiSelectControl(options, fallback) {
  const select = document.createElement("select");
  select.className = "agent-form__input";
  select.multiple = true;
  const chosen = new Set(Array.isArray(fallback) ? fallback : []);
  for (const option of options) select.append(new Option(option.title, option.value, false, chosen.has(option.value)));
  return { element: select, read: () => [...select.selectedOptions].map((o) => o.value) };
}

function checkboxControl(fallback) {
  const box = document.createElement("input");
  box.type = "checkbox";
  box.checked = fallback === true;
  return { element: box, read: () => box.checked };
}

function numberControl(definition, isRequired) {
  const input = document.createElement("input");
  input.type = "number";
  input.className = "agent-form__input";
  input.required = isRequired;
  input.step = definition.type === "integer" ? "1" : "any";
  if (typeof definition.minimum === "number") input.min = String(definition.minimum);
  if (typeof definition.maximum === "number") input.max = String(definition.maximum);
  if (typeof definition.default === "number") input.value = String(definition.default);
  return { element: input, read: () => (input.value === "" ? undefined : Number(input.value)) };
}

function textControl(definition, isRequired) {
  const input = document.createElement("input");
  input.type = FORMAT_INPUT_TYPES[definition.format] ?? "text";
  input.className = "agent-form__input";
  input.required = isRequired;
  input.autocomplete = "off";
  if (typeof definition.minLength === "number") input.minLength = definition.minLength;
  if (typeof definition.maxLength === "number") input.maxLength = definition.maxLength;
  if (typeof definition.default === "string") input.value = definition.default;
  return { element: input, read: () => (input.value === "" && !isRequired ? undefined : input.value) };
}

// Every way the dialog closes answers the request: Submit/Open is the only accept, Decline is a decline, and Esc
// or a close by closeElicitationDialog() is a cancel.
function onClose() {
  const resolve = settle;
  settle = null;
  const answer = mcpElicitationDialog.returnValue === "accept" ? pendingAccept
    : mcpElicitationDialog.returnValue === "decline" ? { action: "decline" }
      : { action: "cancel" };
  pendingAccept = null;
  acceptAnswer = null;
  resolve?.(answer);
}

export function wireElicitation() {
  if (!mcpElicitationDialog) return;
  mcpElicitationSubmitBtn.addEventListener("click", () => {
    const answer = acceptAnswer?.();
    if (!answer) return;
    pendingAccept = answer;
    mcpElicitationDialog.close("accept");
  });
  mcpElicitationDeclineBtn.addEventListener("click", () => mcpElicitationDialog.close("decline"));
  // Enter in a one-field form submits it implicitly, and method="dialog" would then close with an empty
  // returnValue, a cancel that throws the typed value away. Route it through the Submit button instead. A URL
  // request has no inputs, so Enter never opens its link.
  mcpElicitationDialog.querySelector("form")?.addEventListener("submit", (event) => {
    event.preventDefault();
    if (!mcpElicitationSubmitBtn.hidden && mcpElicitationUrlBox.hidden) mcpElicitationSubmitBtn.click();
  });
  mcpElicitationDialog.addEventListener("close", onClose);
}
