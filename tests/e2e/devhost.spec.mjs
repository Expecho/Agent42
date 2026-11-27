// Layer 1 (ADR 0151): browser smoke against the offline DevHost. Proves the seams no unit test sees: every module in the
// ES-module graph is reachable, the service worker's all-or-nothing SHELL installs, and a chat turn streams end to end.
import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const swSource = readFileSync(
  fileURLToPath(new URL("../../src/Agent42.Backend/wwwroot/sw.js", import.meta.url)), "utf8");
const cacheName = /const CACHE = "([^"]+)"/.exec(swSource)?.[1];
const shell = JSON.parse(/const SHELL = (\[[\s\S]*?\]);/.exec(swSource.replace(/\/\/.*$/gm, ""))?.[1] ?? "[]");

const staticPath = /^\/(js|lib|shared)\//;

test("sw.js parses (guards the parser this spec relies on)", () => {
  expect(cacheName).toMatch(/^agent42-shell-v\d+$/);
  expect(shell.length).toBeGreaterThan(40);
  expect(shell).toContain("/js/main.js");
});

test("loads the shell without errors", async ({ page }) => {
  const failed = [];
  const consoleErrors = [];
  page.on("response", (r) => {
    if (staticPath.test(new URL(r.url()).pathname) && !r.ok()) failed.push(`${r.status()} ${r.url()}`);
  });
  page.on("requestfailed", (r) => failed.push(`failed ${r.url()}`));
  page.on("console", (m) => { if (m.type() === "error") consoleErrors.push(m.text()); });
  page.on("pageerror", (e) => consoleErrors.push(String(e)));

  await page.goto("/");
  await expect(page.locator("#promptInput")).toBeVisible();
  await page.waitForLoadState("networkidle");

  expect(failed, "failed static requests").toEqual([]);
  expect(consoleErrors, "console errors").toEqual([]);
});

test("service worker caches the full SHELL", async ({ page }) => {
  await page.goto("/");
  await page.evaluate(() => navigator.serviceWorker.ready);
  const cached = await page.evaluate(async (name) => {
    const cache = await caches.open(name);
    return (await cache.keys()).map((r) => new URL(r.url).pathname);
  }, cacheName);
  const missing = shell.filter((p) => !cached.includes(p));
  expect(missing, `SHELL entries missing from cache ${cacheName}`).toEqual([]);
});

test("streams a reply", async ({ page }) => {
  await page.goto("/");
  const prompt = page.locator("#promptInput");
  await expect(prompt).toBeEnabled();
  await prompt.fill("what is the answer");
  await prompt.press("Enter");

  const reply = page.locator(".msg").last();
  await expect(reply).toContainText("42", { timeout: 30_000 });
  await expect(prompt).toBeEnabled();
});

test("renders the shell offline once installed", async ({ page, context }) => {
  await page.goto("/");
  await page.evaluate(() => navigator.serviceWorker.ready);
  // clients.claim() runs on activate; reload so the page is controlled before going offline.
  await page.reload();
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null);
  await context.setOffline(true);
  await page.reload();
  await expect(page.locator("#promptInput")).toBeVisible();
});
