#!/usr/bin/env node
// PreToolUse hook: blocks agent-initiated git commits on protected branches and
// pushes to/from protected refs. Exit 2 = block tool call, stderr shown to Claude.
// The protected list is read from .agentic/quality-gates.yaml (branches.protected)
// — single source, shared with lefthook.yml; falls back to a safe default.
import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";

const ROOT = process.env.CLAUDE_PROJECT_DIR || process.cwd();
const FALLBACK = ["main", "master", "develop"];

function protectedBranches() {
  try {
    const yaml = readFileSync(join(ROOT, ".agentic", "quality-gates.yaml"), "utf8");
    const m = yaml.match(/^\s*protected:\s*\[([^\]]*)\]/m);
    if (m) {
      const list = m[1]
        .split(",")
        .map((s) => s.trim().replace(/^['"]|['"]$/g, ""))
        .filter(Boolean);
      if (list.length) return list;
    }
  } catch {}
  return FALLBACK;
}

function currentBranch() {
  try {
    return execSync("git branch --show-current", { cwd: ROOT, encoding: "utf8" }).trim();
  } catch {
    return "";
  }
}

function block(msg) {
  console.error(msg);
  process.exit(2);
}

let input = "";
process.stdin.on("data", (d) => (input += d));
process.stdin.on("end", () => {
  let cmd = "";
  try {
    const payload = JSON.parse(input);
    if (payload.tool_name !== "Bash") process.exit(0);
    cmd = String(payload.tool_input?.command ?? "");
  } catch {
    process.exit(0); // never break the session on malformed input
  }

  const isCommit = /\bgit\b[^\n;|&]*\bcommit\b/.test(cmd);
  const isPush = /\bgit\b[^\n;|&]*\bpush\b/.test(cmd);
  if (!isCommit && !isPush) process.exit(0);

  const PROTECTED = protectedBranches();
  const P = PROTECTED.join("|");
  // (?![\w./-]) = end of the branch name: `develop` matches, `develop-x` doesn't.

  // The branch is read BEFORE the tool runs, so `git switch main && git commit`
  // would evade the current-branch checks below. Block the combination outright.
  // (`checkout|switch -b/-c <new> <start>` stays allowed — it lands on the NEW branch.)
  if (new RegExp(`\\bgit\\b[^\\n;|&]*\\b(?:checkout|switch)\\s+(?:${P})(?![\\w./-])`).test(cmd)) {
    block(
      `BLOCKED: this command switches to a protected branch (${PROTECTED.join(", ")}) ` +
        `in the same command as a commit/push. Work on a feature branch instead.`
    );
  }

  const branch = currentBranch();

  if (isCommit && PROTECTED.includes(branch)) {
    block(
      `BLOCKED: you are on protected branch '${branch}'. ` +
        `Create a branch first: git checkout -b feature/<description>`
    );
  }

  if (isPush) {
    // Explicit protected target: `origin main`, `HEAD:main`, `:main`.
    if (new RegExp(`\\bpush\\b[^\\n;|&]*(?:\\s|:)(?:${P})(?![\\w./-])`).test(cmd)) {
      block(
        `BLOCKED: push targets a protected branch (${PROTECTED.join(", ")}). ` +
          `Push a feature branch and open an MR instead.`
      );
    }
    // A bare `git push` (or `git push origin HEAD`) from a protected branch
    // names no ref but still targets that branch.
    if (PROTECTED.includes(branch)) {
      block(
        `BLOCKED: you are on protected branch '${branch}'; a push from here targets it. ` +
          `Move the work to a feature branch and open an MR instead.`
      );
    }
  }

  process.exit(0);
});
