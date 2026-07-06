#!/usr/bin/env node
// PreToolUse hook: blocks agent-initiated git commits on protected branches and
// pushes targeting protected refs. Exit 2 = block tool call, stderr shown to Claude.
// Keep PROTECTED in sync with the branches named in CLAUDE.md "Hard rules".
import { execSync } from "node:child_process";

const PROTECTED = ["main"];

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

  if (isCommit) {
    let branch = "";
    try {
      branch = execSync("git branch --show-current", { encoding: "utf8" }).trim();
    } catch {
      process.exit(0);
    }
    if (PROTECTED.includes(branch)) {
      console.error(
        `BLOCKED: you are on protected branch '${branch}'. ` +
          `Create a branch first: git checkout -b feature/<description>`
      );
      process.exit(2);
    }
  }

  if (isPush) {
    // Block pushes that explicitly target a protected ref (origin main, HEAD:main, :main).
    const target = new RegExp(
      `\\bpush\\b[^\\n;|&]*(?:\\s|:)(${PROTECTED.join("|")})\\b`
    );
    if (target.test(cmd)) {
      console.error(
        `BLOCKED: push targets a protected branch (${PROTECTED.join(", ")}). ` +
          `Push a feature branch and open an MR instead.`
      );
      process.exit(2);
    }
  }

  process.exit(0);
});
