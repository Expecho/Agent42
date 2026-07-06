#!/usr/bin/env node
// Mechanical drift check: every repo path referenced in agent docs must exist.
// Scans CLAUDE.md, .agentic/gotchas.md, and .agentic/decisions/*.md for
// `evidence:` lines, exemplar-table paths, and inline code-quoted paths.
// Exit 1 (fails CI) when a referenced path is missing.
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join } from "node:path";

const root = process.cwd();
const files = ["CLAUDE.md", ".agentic/gotchas.md"];
const adrDir = join(root, ".agentic", "decisions");
if (existsSync(adrDir)) {
  for (const f of readdirSync(adrDir)) {
    if (f.endsWith(".md") && !f.startsWith("0000")) files.push(join(".agentic", "decisions", f));
  }
}

// Path-looking tokens: contain a slash + a dot-extension, quoted in backticks,
// or on an "Evidence:" line. Skip URLs, globs, and placeholders.
const PATH_RE = /`([^`\s]+\/[^`\s]+\.[a-z0-9]{1,10})`/gi;
const SKIP = /^(https?:|.*(\*|\{|<|TODO|EXAMPLE))/i;

let missing = 0;
for (const rel of files) {
  const abs = join(root, rel);
  if (!existsSync(abs)) continue;
  const text = readFileSync(abs, "utf8");
  // Ignore EXAMPLE-marked lines AND whole sections whose heading says EXAMPLE,
  // so template placeholders don't fail the check.
  let inExampleSection = false;
  for (const line of text.split("\n")) {
    if (/^#{1,6}\s/.test(line)) inExampleSection = /EXAMPLE/i.test(line);
    if (inExampleSection || /EXAMPLE/i.test(line)) continue;
    for (const m of line.matchAll(PATH_RE)) {
      const p = m[1];
      if (SKIP.test(p)) continue;
      if (!existsSync(join(root, p))) {
        console.error(`MISSING: ${p}  (referenced in ${rel})`);
        missing++;
      }
    }
  }
}

if (missing > 0) {
  console.error(`\n${missing} referenced path(s) no longer exist. Run /audit-docs to fix the docs.`);
  process.exit(1);
}
console.log("check-doc-paths: all referenced paths exist.");
