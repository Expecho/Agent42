// Layer 3 (ADR 0151): the self-host compose stack (Postgres + file volume, no auth) with a stub LLM. One chat round-trip
// through the real NDJSON endpoint, then an `app` restart and the conversation must still be there (ADR 0127).
import { test, expect } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";

const repoRoot = fileURLToPath(new URL("../..", import.meta.url));
const composeFiles = ["compose.yaml", "compose.build.yaml", "tests/e2e/selfhost/compose.e2e.yaml"];
const stubText = "Stub reply from the e2e LLM: forty-two.";
const model = "e2estub:stub-model";

const compose = (...args) =>
  execFileSync("docker", ["compose", ...composeFiles.flatMap((f) => ["-f", f]), ...args],
    { cwd: repoRoot, stdio: "inherit" });

async function waitReady(request, timeoutMs = 120_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      if ((await request.get("/api/conversations")).ok()) return;
    } catch { /* not listening yet */ }
    if (Date.now() > deadline) throw new Error("self-host app did not become ready");
    await new Promise((r) => setTimeout(r, 2000));
  }
}

test.describe.configure({ mode: "serial" });

test.setTimeout(300_000);

let conversationId;

test("chat round-trip", async ({ request }) => {
  await waitReady(request);

  const provider = await request.post("/api/providers", {
    data: {
      key: "e2estub", kind: "OpenAICompatible", displayName: "E2E stub",
      endpoint: "http://llm-stub:8099/v1", apiKey: "not-a-secret",
      models: [{ id: "stub-model", displayName: "Stub", supportsTools: false, supportsVision: false }],
      useResponsesApi: false,
    },
  });
  expect(provider.ok(), await provider.text()).toBeTruthy();
  const def = await request.put("/api/default-model", { data: { default: model } });
  expect(def.ok(), await def.text()).toBeTruthy();

  conversationId = randomUUID();
  const chat = await request.post(`/api/chat/${conversationId}`, { data: { message: "hello", model } });
  expect(chat.ok(), await chat.text()).toBeTruthy();
  expect(await chat.text()).toContain("forty-two");
});

test("persists across restart", async ({ request }) => {
  compose("restart", "app");
  await waitReady(request);

  const res = await request.get(`/api/conversations/${conversationId}/messages`);
  expect(res.ok()).toBeTruthy();
  expect(await res.text()).toContain(stubText.split(":")[0]);
});
