import { test } from "node:test";
import assert from "node:assert/strict";
import {
  Answer, CANCELLED_TEXT, DECLINED_TEXT, TIMED_OUT_TEXT, TOO_LARGE_TEXT, containsUrlLikeString, createGate, sanitize,
} from "../lib/gate.js";

function harness({ answers = [], yolo = false, timeoutMs } = {}) {
  const asked = [];
  const set = new Set();
  const gate = createGate({
    prompt: async (request) => {
      asked.push(request);
      const next = answers.shift();
      return typeof next === "function" ? next() : next;
    },
    isYolo: () => yolo,
    approved: { has: (n) => set.has(n), add: (n) => void set.add(n) },
    timeoutMs,
  });
  return { gate, asked, set };
}

test("a read-only call without URL arguments passes unprompted", async () => {
  const { gate, asked } = harness();
  assert.deepEqual(await gate.review("snapshot", { tabId: 1 }, { readOnly: true }), { allowed: true });
  assert.equal(asked.length, 0);
});

test("a read-only call with a URL-shaped argument prompts", async () => {
  const { gate, asked } = harness({ answers: [Answer.DENY] });
  const decision = await gate.review("snapshot", { tabId: 1, note: "https://evil.example/?d=x" }, { readOnly: true });
  assert.deepEqual(decision, { allowed: false, text: DECLINED_TEXT });
  assert.equal(asked.length, 1);
});

test("a write prompts, and a deny carries the declined text", async () => {
  const { gate, asked } = harness({ answers: [Answer.DENY] });
  assert.deepEqual(await gate.review("click", { tabId: 1, ref: "e1" }, {}), { allowed: false, text: DECLINED_TEXT });
  assert.equal(asked[0].tool, "click");
});

test("allow once does not carry over to the next call", async () => {
  const { gate, asked } = harness({ answers: [Answer.ONCE, Answer.DENY] });
  assert.equal((await gate.review("click", {}, {})).allowed, true);
  assert.equal((await gate.review("click", {}, {})).allowed, false);
  assert.equal(asked.length, 2);
});

test("always stops asking for that tool only", async () => {
  const { gate, asked } = harness({ answers: [Answer.ALWAYS, Answer.DENY] });
  assert.equal((await gate.review("click", {}, {})).allowed, true);
  assert.equal((await gate.review("click", {}, {})).allowed, true);
  assert.equal((await gate.review("click", { x: 1 }, {})).allowed, true);
  assert.equal((await gate.review("navigate", {}, {})).allowed, false);
  assert.equal(asked.length, 2);
});

// The mutation-checked test (the plan's Step 3): drop the post-lock re-check and this asks five times.
test("calls queued behind the prompt that approved them are released, with one prompt", async () => {
  let answer;
  const { gate, asked } = harness({ answers: [() => new Promise((r) => (answer = r))] });
  const calls = Array.from({ length: 5 }, () => gate.review("type", { text: "x" }, {}));
  await new Promise((r) => setTimeout(r, 20));
  answer(Answer.ALWAYS);
  const decisions = await Promise.all(calls);
  assert.ok(decisions.every((d) => d.allowed));
  assert.equal(asked.length, 1);
});

test("the raw tool name is recorded, not the sanitized one shown", async () => {
  const raw = "cli‮ck";
  const { gate, asked, set } = harness({ answers: [Answer.ALWAYS] });
  await gate.review(raw, {}, {});
  assert.equal(asked[0].tool, "click");
  assert.ok(set.has(raw));
  assert.ok(!set.has("click"));
});

test("only the three answers allow anything: anything else denies", async () => {
  for (const answer of ["yes", "y", "ALWAYS", "", undefined, 1, "allow"]) {
    const { gate } = harness({ answers: [answer] });
    assert.equal((await gate.review("click", {}, {})).allowed, false, `answer ${JSON.stringify(answer)}`);
  }
});

test("no answer within the timeout denies with the timed-out text", async () => {
  const { gate } = harness({ answers: [() => new Promise(() => {})], timeoutMs: 30 });
  assert.deepEqual(await gate.review("click", {}, {}), { allowed: false, text: TIMED_OUT_TEXT });
});

// Critic L1: on a timeout the prompt is told to close (its signal aborts), so a late click cannot look accepted.
test("a timeout aborts the prompt's signal", async () => {
  let seen;
  const gate = createGate({
    prompt: (_request, signal) => {
      seen = signal;
      return new Promise(() => {});
    },
    isYolo: () => false,
    approved: { has: () => false, add: () => {} },
    timeoutMs: 20,
  });
  await gate.review("click", {}, {});
  assert.equal(seen.aborted, true);
});

// Critic M4: the console helper cancels a pending prompt when its channel goes; so does this gate.
test("cancelling a waiting call denies it with the cancelled text and closes its prompt", async () => {
  let seen;
  const gate = createGate({
    prompt: (_request, signal) => {
      seen = signal;
      return new Promise(() => {});
    },
    isYolo: () => false,
    approved: { has: () => false, add: () => {} },
  });
  const cancel = new AbortController();
  const decision = gate.review("click", {}, {}, cancel.signal);
  await new Promise((r) => setTimeout(r, 10));
  cancel.abort();
  assert.deepEqual(await decision, { allowed: false, text: CANCELLED_TEXT });
  assert.equal(seen.aborted, true);
});

test("a call cancelled while queued behind another prompt is never shown", async () => {
  let release;
  const { gate, asked } = harness({ answers: [() => new Promise((r) => (release = r))] });
  const first = gate.review("click", {}, {});
  const cancel = new AbortController();
  const second = gate.review("type", {}, {}, cancel.signal);
  await new Promise((r) => setTimeout(r, 10));
  cancel.abort();
  release(Answer.ONCE);
  assert.equal((await first).allowed, true);
  assert.deepEqual(await second, { allowed: false, text: CANCELLED_TEXT });
  assert.equal(asked.length, 1);
});

test("the prompt shows the sanitized target", async () => {
  const { gate, asked } = harness({ answers: [Answer.DENY] });
  await gate.review("click", { tabId: 1, ref: "e1" }, { target: "Tab 1: Ba‮nk — https://bank.example/" });
  assert.equal(asked[0].target, "Tab 1: Bank — https://bank.example/");
});

test("a prompt that throws (the approval window was closed) denies", async () => {
  const { gate } = harness({ answers: [() => Promise.reject(new Error("closed"))] });
  assert.deepEqual(await gate.review("click", {}, {}), { allowed: false, text: DECLINED_TEXT });
});

test("arguments over the review limit are denied without being shown", async () => {
  const { gate, asked } = harness();
  const decision = await gate.review("type", { text: "x".repeat(20_001) }, {});
  assert.deepEqual(decision, { allowed: false, text: TOO_LARGE_TEXT });
  assert.equal(asked.length, 0);
});

test("evaluate (autoRun) never prompts — the user decision", async () => {
  const { gate, asked } = harness();
  assert.equal((await gate.review("evaluate", { expression: "location.href='https://x'" }, { autoRun: true })).allowed, true);
  assert.equal(asked.length, 0);
});

test("yolo passes everything without a prompt", async () => {
  const { gate, asked } = harness({ yolo: true });
  assert.equal((await gate.review("navigate", { url: "https://x" }, {})).allowed, true);
  assert.equal((await gate.review("type", { text: "x".repeat(30_000) }, {})).allowed, true);
  assert.equal(asked.length, 0);
});

test("arguments are shown in full and sanitized; newlines survive only in the argument block", async () => {
  const { gate, asked } = harness({ answers: [Answer.DENY] });
  await gate.review("ty\u001b[2Kpe", { text: "a‮b\u0000c", long: "z".repeat(5000) }, {});
  assert.equal(asked[0].tool, "ty[2Kpe");
  assert.ok(asked[0].args.includes("\n"));
  // JSON.stringify already escapes NUL as the text \u0000 (as the helper's serializer does); the bidi override
  // is not escaped by JSON, so sanitize is what removes it.
  assert.ok(asked[0].args.includes("ab\\u0000c"));
  assert.ok(!asked[0].args.includes("‮"));
  assert.ok(asked[0].args.includes("z".repeat(5000)));
  assert.equal(sanitize("a\nb", false), "ab");
});

test("URL detection matches the helper: scheme: or //host, after leading whitespace", () => {
  assert.equal(containsUrlLikeString({ a: [{ b: "  javascript:alert(1)" }] }), true);
  assert.equal(containsUrlLikeString("//evil.example"), true);
  assert.equal(containsUrlLikeString({ a: "plain text: with a colon" }), false);
  assert.equal(containsUrlLikeString({ n: 3, b: true }), false);
});
