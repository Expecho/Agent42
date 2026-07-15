#!/usr/bin/env node
// Mechanical drift check: every repo path referenced in agent docs must exist.
// Scans CLAUDE.md, .agentic/gotchas.md (plus .agentic/gotchas/*.md if split by
// area), and .agentic/decisions/*.md for `evidence:` lines, exemplar-table paths,
// and inline code-quoted paths. Exit 1 (fails CI) when a referenced path is missing.
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join } from "node:path";

const root = process.cwd();
const files = ["CLAUDE.md", ".agentic/gotchas.md"];
for (const dir of [join(".agentic", "gotchas"), join(".agentic", "decisions")]) {
  const abs = join(root, dir);
  if (!existsSync(abs)) continue;
  for (const f of readdirSync(abs)) {
    if (f.endsWith(".md") && !f.startsWith("0000")) files.push(join(dir, f));
  }
}

// Path-looking tokens quoted in backticks. PATH_RE (any line) requires a slash +
// dot-extension; ROOT_RE additionally catches root-level files (`lefthook.yml`,
// `Directory.Packages.props`) but only on evidence lines and table rows, where a
// bare filename is a claim rather than prose. Skip URLs, globs, and placeholders.
//
// The extension must START WITH A LOWERCASE LETTER (then lowercase letters/digits).
// Real file extensions are lowercase (`.cs`, `.md`, `.mjs`, `.props`, `.yaml`); this
// deliberately excludes two non-path shapes that are legitimately backticked in these
// docs and were being misread as missing paths: `Type.Member` refs (`ModelId.Parse`,
// `Provider.Foundry` — a PascalCase "extension") and `publisher/model.version` ids
// (`openai/gpt-4.1` — a numeric "extension"). The regexes are case-sensitive (no `i`)
// so the extension test is meaningful; base names keep uppercase via the negated class.
const PATH_RE = /`([^`\s]+\/[^`\s]+\.[a-z][a-z0-9]{0,9})`/g;
const ROOT_RE = /`([^`\s/\\]+\.[a-z][a-z0-9]{0,9})`/g;
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
    const isClaimLine = /\bevidence\b/i.test(line) || /^\s*\|/.test(line);
    const tokens = [...line.matchAll(PATH_RE)].map((m) => m[1]);
    if (isClaimLine) tokens.push(...[...line.matchAll(ROOT_RE)].map((m) => m[1]));
    for (const p of tokens) {
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
