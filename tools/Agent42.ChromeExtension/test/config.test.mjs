import { test } from "node:test";
import assert from "node:assert/strict";
import { defaultBlockedSites, hardFloorRefusal, isBlocked, normalizeServer, parseBlockedSites } from "../lib/config.js";

test("the server is https, or http on the loopback only", () => {
  assert.equal(normalizeServer(" https://chat.agent42.nl/some/path "), "https://chat.agent42.nl");
  assert.equal(normalizeServer("http://localhost:61326/"), "http://localhost:61326");
  assert.equal(normalizeServer("http://127.0.0.1:61326"), "http://127.0.0.1:61326");
  assert.equal(normalizeServer("http://chat.agent42.nl"), null);
  assert.equal(normalizeServer("ws://chat.agent42.nl"), null);
  assert.equal(normalizeServer("not a url"), null);
  assert.equal(normalizeServer(undefined), null);
});

test("blocked sites: one host per line, comments, pasted URLs reduced to hosts, de-duplicated", () => {
  assert.deepEqual(parseBlockedSites("bank.example\n# a comment\nhttps://Mail.Example.com/inbox\n\nbank.example  # again\n"), [
    "bank.example", "mail.example.com",
  ]);
});

test("a blocked host covers its subdomains, not name stems", () => {
  const hosts = ["bank.example"];
  assert.equal(isBlocked("https://bank.example/", hosts), true);
  assert.equal(isBlocked("https://login.bank.example/x", hosts), true);
  assert.equal(isBlocked("https://notbank.example/", hosts), false);
  assert.equal(isBlocked("::garbage", hosts), true, "an unreadable address fails closed");
});

test("the default blocked list is the Agent42 server itself", () => {
  assert.equal(defaultBlockedSites("https://chat.agent42.nl"), "chat.agent42.nl");
});

test("the hard floor: extension, browser-internal and Web Store pages", () => {
  for (const url of [
    "chrome-extension://ddfhnmhjjdmlfpidpampclimjhnmmnmm/options.html",
    "chrome-extension://someotherextensionidxxxxxxxxxxxxx/x.html",
    "chrome://extensions", "chrome-untrusted://terminal", "devtools://devtools/bundled/x.html",
    "view-source:https://example.com", "https://chromewebstore.google.com/", "https://chrome.google.com/webstore/detail/x",
    "not a url",
  ]) {
    assert.ok(hardFloorRefusal(url), url);
  }
  for (const url of ["https://example.com/", "http://localhost:3000/", "https://chrome.google.com/search", "about:blank", "blob:https://example.com/abc"]) {
    assert.equal(hardFloorRefusal(url), null, url);
  }
});

// Fail closed on everything that is not an http(s) page: local files, data, srcdoc, and blobs of non-web origins.
test("the floor refuses every non-web scheme", () => {
  for (const url of ["file:///C:/Users/x/secrets.txt", "data:text/html,<b>x</b>", "about:srcdoc", "about:newtab", "javascript:alert(1)",
    "blob:chrome-extension://ddfhnmhjjdmlfpidpampclimjhnmmnmm/x", "ftp://example.com/", ""]) {
    assert.ok(hardFloorRefusal(url), url);
  }
});

test("a blob: is blocked by the site that created it", () => {
  assert.equal(isBlocked("blob:https://login.bank.example/7d1c", ["bank.example"]), true);
  assert.equal(isBlocked("blob:https://example.com/7d1c", ["bank.example"]), false);
  assert.equal(isBlocked("about:blank", ["bank.example"]), false);
});
