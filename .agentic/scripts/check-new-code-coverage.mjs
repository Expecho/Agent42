#!/usr/bin/env node
// Coverage gate for NEW CODE only (`coverage_new_code_pct_min` in .agentic/quality-gates.yaml).
//
// Whole-repo coverage is the wrong number to gate on: it moves glacially, so it neither fails when a
// change is untested nor passes meaningfully when it is. This measures the lines this branch actually
// ADDED OR CHANGED — the "clean as you code" shape — by intersecting `git diff` with the cobertura
// report coverlet writes.
//
// Usage:
//   dotnet test Agent42.slnx --collect:"XPlat Code Coverage"
//   node .agentic/scripts/check-new-code-coverage.mjs [baseRef]
//
// Exit 1 (fails CI) when the ratio is under the threshold. No npm dependencies, matching
// check-doc-paths.mjs — the only other script here.
import { readFileSync, readdirSync, existsSync, statSync } from "node:fs";
import { join } from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const base = process.argv[2] || process.env.BASE_BRANCH || "origin/main";

// ---- threshold: read from the gate file, never hardcoded (hard rule 3) ----
const gates = readFileSync(join(root, ".agentic/quality-gates.yaml"), "utf8");
const thresholdMatch = gates.match(/^\s*coverage_new_code_pct_min:\s*(\d+(?:\.\d+)?)/m);
if (!thresholdMatch) {
  console.error("coverage_new_code_pct_min is not defined in .agentic/quality-gates.yaml — nothing to enforce.");
  process.exit(1);
}
const threshold = Number(thresholdMatch[1]);

// ---- the lines this branch added or changed ----
function git(...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
}

let mergeBase;
try {
  mergeBase = git("merge-base", "HEAD", base).trim();
} catch {
  console.error(`Cannot resolve a merge base with '${base}'. Pass one as argv[2] or set BASE_BRANCH.`);
  process.exit(1);
}

// -U0 so the hunk headers bound exactly the changed lines, with no context bleeding in.
const diff = git("diff", "-U0", "--diff-filter=ACMR", `${mergeBase}...HEAD`);

// Only production code is gated. Test projects are excluded on purpose: a test file's own coverage is
// a tautology (running it covers it), so including them inflates the number and hides the gap.
const isGated = (p) =>
  p.startsWith("src/") && p.endsWith(".cs") && !/\.(g|Designer)\.cs$/.test(p) && !/\/(bin|obj)\//.test(p);

const changed = new Map(); // repo-relative path -> Set of 1-based line numbers
let current = null;
for (const line of diff.split("\n")) {
  if (line.startsWith("+++ b/")) {
    const path = line.slice(6).trim();
    current = isGated(path) ? (changed.get(path) ?? new Set()) : null;
    if (current) changed.set(path, current);
    continue;
  }
  if (!current || !line.startsWith("@@")) continue;
  // @@ -old,count +new,count @@
  const hunk = line.match(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/);
  if (!hunk) continue;
  const start = Number(hunk[1]);
  const count = hunk[2] === undefined ? 1 : Number(hunk[2]);
  for (let i = 0; i < count; i++) current.add(start + i);
}

if (changed.size === 0) {
  console.log("check-new-code-coverage: no production code changed — nothing to gate.");
  process.exit(0);
}

// ---- the cobertura report ----
function newestReport(dir) {
  if (!existsSync(dir)) return null;
  let best = null;
  for (const entry of readdirSync(dir)) {
    const candidate = join(dir, entry, "coverage.cobertura.xml");
    if (!existsSync(candidate)) continue;
    const at = statSync(candidate).mtimeMs;
    if (!best || at > best.at) best = { path: candidate, at };
  }
  return best?.path ?? null;
}

const report = newestReport(join(root, "tests/Agent42.Tests/TestResults"));
if (!report) {
  console.error(
    "No coverage.cobertura.xml found under tests/Agent42.Tests/TestResults.\n" +
    'Run: dotnet test Agent42.slnx --collect:"XPlat Code Coverage"');
  process.exit(1);
}

// filename -> Map(line -> hits). One file can appear as several <class> elements (one per type), so
// hits are merged rather than overwritten.
const hits = new Map();
const xml = readFileSync(report, "utf8");
for (const [, filename, body] of xml.matchAll(/<class [^>]*filename="([^"]+)"[^>]*>([\s\S]*?)<\/class>/g)) {
  const normalized = filename.replace(/\\/g, "/");
  const lines = hits.get(normalized) ?? new Map();
  hits.set(normalized, lines);
  for (const [, number, count] of body.matchAll(/<line number="(\d+)" hits="(\d+)"/g)) {
    lines.set(Number(number), (lines.get(Number(number)) ?? 0) + Number(count));
  }
}

// Cobertura filenames are project-relative; changed paths are repo-relative. Match on suffix, which is
// unambiguous here because every path carries its project folder.
function linesFor(path) {
  for (const [filename, lines] of hits) {
    if (path.endsWith(filename) || filename.endsWith(path)) return lines;
  }
  return null;
}

let covered = 0;
let total = 0;
const rows = [];
for (const [path, changedLines] of [...changed].sort()) {
  const lines = linesFor(path);
  if (!lines) continue; // no coverable lines recorded (interfaces, records, pure declarations)
  let fileCovered = 0;
  let fileTotal = 0;
  for (const line of changedLines) {
    if (!lines.has(line)) continue; // not a coverable line (comment, brace, blank)
    fileTotal++;
    if (lines.get(line) > 0) fileCovered++;
  }
  if (fileTotal === 0) continue;
  covered += fileCovered;
  total += fileTotal;
  rows.push([path, fileCovered, fileTotal]);
}

if (total === 0) {
  console.log("check-new-code-coverage: no coverable new lines — nothing to gate.");
  process.exit(0);
}

const pct = (covered / total) * 100;
rows.sort((a, b) => a[1] / a[2] - b[1] / b[2]);
for (const [path, c, t] of rows) {
  console.log(`  ${((c / t) * 100).toFixed(1).padStart(5)}%  ${String(c).padStart(4)}/${String(t).toString().padEnd(4)}  ${path}`);
}
console.log(`\nNew-code coverage: ${covered}/${total} = ${pct.toFixed(1)}% (threshold ${threshold}%, vs ${base})`);

if (pct + 1e-9 < threshold) {
  console.error(
    `\ncoverage_new_code_pct_min is ${threshold}% (.agentic/quality-gates.yaml) — this branch is at ${pct.toFixed(1)}%.`);
  process.exit(1);
}
console.log("check-new-code-coverage: PASS");
