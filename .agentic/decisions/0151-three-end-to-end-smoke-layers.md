---
id: 0151
date: 2026-11-27
status: accepted
tags: [testing, ci, e2e, deploy, self-host, quality-gates]
supersedes: []
extends: [0050, 0127, 0130]
amends: []
---

# Three end-to-end smoke layers (browser vs DevHost, post-deploy probe, self-host stack)

## Context
The suite is Orleans TestCluster, API-fixture and source-text guards. Nothing drives a real browser, nothing probes a
deployed revision, and nothing runs the self-host compose stack. Many ADRs and gotchas end in "prod smoke" for exactly
these seams (`SHELL` all-or-nothing, Easy Auth `excludedPaths`, `Storage:Provider=local`).

## Decision
Three thin, secret-free layers, each a CI job:

1. **Browser smoke vs DevHost** (`e2e-devhost` in `pr-checks.yml`, every PR): Playwright (Chromium) in `tests/e2e/`
   against `tools/Agent42.DevHost` (offline canned LLM). Asserts the shell loads with no failed module or console error,
   the service worker caches every `SHELL` entry, and a sent message streams the canned reply.
2. **Post-deploy smoke** (`smoke` job after `deploy` in `deploy.yml`): `infra/smoke-test.sh <base-url>`, curl only. It probes
   the Easy-Auth-excluded public paths (200), a **negative** probe (an unauthenticated `GET /api/conversations` must not be
   200, which proves Easy Auth is in front) and `POST /api/email-in` (must not be 404). `/health` cannot be used: it is mapped
   only in Development and the Bicep defines no ACA probes.
3. **Self-host smoke** (`e2e-selfhost.yml`): the compose stack built from source plus an OpenAI-compatible stub LLM,
   registered as a per-user provider (ADR 0018/0019: there is no operator model). One chat round-trip, then
   `docker compose restart app` and the conversation is still there (Postgres persistence, ADR 0127).

**Node Playwright, not .NET.** CI already runs Node 22, the same specs target DevHost and the compose app by base URL, and
`dotnet test` stays free of a browser download. Rejected: `Microsoft.Playwright` in `Agent42.Tests`.

**No CI identity for the post-deploy layer.** The prod app trusts the Easy Auth principal header; a CI identity would need an
Entra app and a role assignment, and RBAC changes are never executed by the agent (hard rule 5). Unauthenticated probes cover
the revision being up, the static shell served, the exclusion list intact and the auth wall intact. Authenticated flows stay manual.

**Smoke, not feature tests.** These layers prove seams, not behaviour. Feature coverage stays in `Agent42.Tests`.

**The gate is a job result, not a computed number.** `e2e_failing_tests_max: 0` names `tests/e2e/playwright.config.mjs` as its
tool; the measurement is Playwright's exit status in the CI job (the same shape as `vulnerable_packages: 0`). The key is numeric
only because `QualityGateTests` requires it.

## Consequences
- A change to `infra/smoke-test.sh` falls under `infra/**` and triggers a full prod deploy (accepted).
- The smoke probes the revision FQDN from the `backendUrl` Bicep output, never a tenant literal (`InfraPortabilityTests`).
- `E2eWorkflowTests` pins the wiring and that the smoke's public paths are excluded by the Bicep `excludedPaths`.
- A `wwwroot/js/` change that breaks `SHELL` now fails a PR instead of a deploy; fixing it still needs a `CACHE` bump in `sw.js`.
