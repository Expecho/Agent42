# Plan: three end-to-end smoke layers

planned-against: main @ 0728b69

## Overview

The suite (`tests/Agent42.Tests`, ~340 files) is Orleans TestCluster, API-fixture and source-text guards. Nothing
drives a real browser, nothing probes a deployed revision, and nothing runs the self-host compose stack. Many ADRs and
gotchas end in "prod smoke" for exactly these seams (SHELL all-or-nothing, Easy Auth `excludedPaths`, `Storage:Provider=local`).
Add three thin, secret-free layers that turn those manual checks into CI jobs:

1. **Browser smoke vs DevHost** (every PR): Playwright, Chromium, offline canned LLM.
2. **Post-deploy smoke** (after `deploy` in `deploy.yml`): curl against the live FQDN, no credentials.
3. **Self-host smoke** (compose + Postgres, stub LLM): one chat round-trip plus a restart-persistence check.

## Affected areas (verified on the tree)

- New: `tests/e2e/` (Node + `@playwright/test`), `tests/e2e/selfhost/` (compose override + LLM stub), `infra/smoke-test.sh`.
- Edit: `.github/workflows/pr-checks.yml`, `.github/workflows/deploy.yml` (a job after `deploy`), new `.github/workflows/e2e-selfhost.yml`.
- Edit: `.agentic/quality-gates.yaml`, `tests/Agent42.Tests/QualityGateTests.cs` (tooling map), `CLAUDE.md` (Commands table).
- Read-only references: `tools/Agent42.DevHost/Program.cs` (ports 61326/61325 in `Properties/launchSettings.json`),
  `src/Agent42.Backend/wwwroot/sw.js` (`SHELL`, `CACHE`), `compose.yaml` / `compose.build.yaml`,
  `infra/modules/container-app.bicep` (`excludedPaths`, ~line 467), `src/Agent42.Backend/Conversations/ChatContracts.cs` (`ChatRequest`),
  `src/Agent42.Backend/Models/ModelProviderContracts.cs` (`ProviderUpsertRequest`).

## Context & constraints (what the executor cannot cheaply re-derive)

- **`/health` and `/alive` exist only when `IsDevelopment()`** (`src/Agent42.ServiceDefaults/Extensions.cs:195-215`), and the
  Bicep defines no ACA probes. So the post-deploy smoke cannot use a health endpoint. Use the Easy-Auth-excluded public
  paths instead: `/legal/privacy.html`, `/shared/` (200), plus a **negative** probe: an unauthenticated `GET /api/conversations`
  must NOT return 200 (401 or a 302 to the login) — that proves Easy Auth is actually in front. `/api/email-in` unauthenticated must
  not be 404. The route is `MapPost` (`Email/AgentApi.Email.cs:27`), so probe with **POST and an empty body** and accept 400/401/403/503
  (bad signature, or no Mailgun key); a GET would give 405, which proves less. Never accept 404 or 200.
- **No CI authentication is designed on purpose.** The prod app trusts the Easy Auth principal header; minting a CI identity would
  mean an Entra app + a role assignment, and hard rule 5 forbids the agent running RBAC changes. Unauthenticated probes cover
  the high-value regressions (revision up, static shell served, exclusion list intact, auth wall intact). Authenticated
  flows stay manual. (Decision for the ADR.)
- **Self-host has no model.** ADR 0019/0030: no operator model; a user with none gets a gated composer. The compose stack therefore
  needs a **stub OpenAI-compatible endpoint** registered as a per-user provider through `POST /api/providers`
  (kind OpenAI-compatible) and made the default, before `POST /api/chat/{id}`. **Risk to verify first:** the provider-endpoint
  validation is NOT a risk (critic-verified): the user provider endpoint is deliberately not SSRF-guarded; it only needs an absolute
  http/https URL (gotchas.md ~958-971, pinned by `ProvidersApiTests.AcceptsLocalhost_RejectsMalformed`), so `http://llm-stub:PORT` is accepted.
  Do not confuse it with the webhook egress guard (gotchas.md ~974), which has no opt-out.
- DevHost's wwwroot path is built from `AppContext.BaseDirectory` (`Program.cs:12`), so it does not depend on the cwd. It has no auth and no
  `AddServiceDefaults` (gotchas.md ~166, 2215): do not add `/health` assertions against it.
- **Node, not .NET, for the browser tests**: the repo already runs Node 22 in CI (`pr-checks.yml`, `ChromeExtensionScriptTests`),
  the same spec files can target DevHost (layer 1) and the compose app (layer 3) by base URL, and `dotnet test` stays free of a
  browser download. Rejected: `Microsoft.Playwright` in `Agent42.Tests` (browser install in every local `dotnet test`).
- **Packages are resolved, never recalled** (hard rule 4): `npm install -D @playwright/test@latest`, commit the lockfile.
- **SHELL/CACHE** (gotchas.md ~2280): this plan changes no `wwwroot/js/` file. If the browser test exposes a SHELL
  mismatch, fixing it needs a `CACHE` bump in `sw.js`.
- **Gate admission (ADR 0050, hard rule 3):** a gate must name a measuring tool that exists, and `QualityGateTests` requires every
  `gates:` key to be numeric (`^\s*key:\s*\d`) and in its tooling map. Use `e2e_failing_tests_max: 0`, tool
  `tests/e2e/playwright.config.mjs` (the file Playwright reads; CI job exit code is the measurement). Do not estimate an actual value.
- Single replica / Easy Auth / egress rules (CLAUDE.md cross-cutting 4, 12, 13) are untouched: no infra changes.
- Governing ADRs: 0002 (auth, DevHost), 0042 (`excludedPaths`), 0050 (gates), 0127 (self-host), 0128 (deploy variables), 0130 (published images),
  0041 (ES modules / SHELL). Next free ADR number: 0151 (re-check at execution; `AdrNumberingTests`).

## Steps

0. **ADR via `/decide` (0151):** record the three layers, Node Playwright over .NET, no-credentials post-deploy probes
   (and why no CI identity), stub provider for self-host, and "smoke only, not feature tests". Add the Feature-map row (Layout and quality).
1. **Scaffold `tests/e2e/`** — `package.json`, lockfile, `playwright.config.mjs` (Chromium only; `E2E_BASE_URL`; a `webServer`
   entry with `cwd` = repo root (Playwright's cwd is `tests/e2e`) running `dotnet run --project tools/Agent42.DevHost` **with** the launch
   profile (port 61326 and `ASPNETCORE_ENVIRONMENT=Development` come only from `Properties/launchSettings.json`; `Program.cs` sets no URLs, so
   `--no-launch-profile` would listen on 5000 as Production), `reuseExistingServer: !process.env.CI`). Confirm DevHost builds under the CI SDK
   (memory: SDK 11 broke the DevHost build locally once).
   Verify: `npx playwright test --list` from `tests/e2e`.
2. **Layer 1 spec `devhost.spec.mjs`** — (a) `/` loads with zero failed `/js/*`, `/lib/*`, `/shared/*` responses and no `console.error`;
   (b) wait for the service worker to activate, then assert the cache named `CACHE` (parsed from `sw.js`) holds every `SHELL` entry;
   (c) type a message, press Enter, assert the canned reply streams into the transcript and the composer re-enables;
   (d) reload offline-after-install and assert the shell still renders. Verify: run it; then deliberately break (rename a module,
   revert) to prove (a) and (b) fail, and record that in the MR. *Parallelizable with step 4.*
3. **Layer 3 stack + spec** — `tests/e2e/selfhost/compose.e2e.yaml` (adds `llm-stub`: `node:22-alpine` running `llm-stub.mjs`, a ~40-line
   OpenAI chat-completions SSE responder; sets the two secrets via env so no `.env` is needed), `selfhost.spec.mjs`:
   wait for `http://127.0.0.1:8080` ready → register the stub provider → set default → send a chat via the NDJSON endpoint, assert
   the stub's text arrives → `docker compose restart app` → `GET /api/conversations/{id}/messages` still returns it (Postgres persistence).
   `compose down -v` in teardown. Verify locally with `docker compose -f compose.yaml -f compose.build.yaml -f tests/e2e/selfhost/compose.e2e.yaml up -d --build`.
   First action: settle the SSRF/endpoint-validation risk above. *Depends on 1.*
4. **Layer 2 `infra/smoke-test.sh <base-url>`** — pure curl/bash (`set -euo pipefail`), retries up to ~5 min for the revision rollover
   (single replica overlaps two briefly, CLAUDE.md rule 12), asserts the probes in "Context". Flag `--no-auth-wall` skips the negative
   probe AND the `/api/email-in` probe (DevHost may not map it) so the script can be run against DevHost for a local dry run.
   In `deploy.yml` add job `smoke` (`needs: deploy`). The `deploy` job has no `outputs:` today, but `infra/main.bicep:345` defines
   `output backendUrl` (unconsumed): capture it from the `az deployment group create` step as a step + job output (`--query properties.outputs.backendUrl.value -o tsv`),
   and give `smoke` its own `azure/login` step (login is per job). No new secrets. Probe the revision FQDN, never a tenant literal: `InfraPortabilityTests`
   forbids `agent42.nl` in `deploy.yml`. Note: a change to `infra/smoke-test.sh` falls under `infra/**` and triggers a full prod deploy (accepted). Verify: dry-run against DevHost; `act`-free review of the YAML;
   `bicep`/workflow guard test (step 6). *Parallelizable with 2 and 3.*
5. **CI wiring** — `pr-checks.yml`: job `e2e-devhost` (setup-dotnet 11.0.x, setup-node 22, `npm ci`, `npx playwright install --with-deps chromium`,
   `npx playwright test devhost`, upload the Playwright report on failure). New `e2e-selfhost.yml`: `pull_request` with `paths:`
   (`src/**`, `compose*.yaml`, `tests/e2e/**`, `Directory.Packages.props`) + nightly `schedule` + `workflow_dispatch`, builds from source
   (`compose.build.yaml`), because the published image is not the PR's code. Verify: push the branch and read the runs.
6. **Gate + guard tests** — add `e2e_failing_tests_max: 0` to `quality-gates.yaml`, the `(key, tool)` row in `QualityGateTests`, a
   `E2eWorkflowTests` guard (source-text, like `DeployTriggerTests`): `pr-checks.yml` has `e2e-devhost`, `deploy.yml` `smoke` needs `deploy`,
   and every path the smoke script treats as public is excluded by the Bicep `excludedPaths`, using a small matcher that implements the
   Bicep rule (exact match unless the entry ends in `/*`; reuse the helper in `EasyAuthExclusionTests` if it is reusable) so the two cannot drift.
   Also extend the `enforcement.ci` comment in `quality-gates.yaml` to list the e2e jobs. Update `CLAUDE.md` Commands (e2e, smoke) and `docs/` self-hosting page with one line each.
   Verify: `dotnet test Agent42.slnx --filter "QualityGate|E2eWorkflow"`.
7. **Close out** — `/gotcha` for anything non-obvious found (probably the dev-only `/health`), `/sync-docs`, full `dotnet test`, `dotnet format --verify-no-changes`, move this spec to `completed/`.

## Acceptance criteria → test mapping

| AC | Proof | Lives in |
|----|-------|----------|
| DevHost UI loads with no failed module/lib request or console error | `loads the shell without errors` | `tests/e2e/devhost.spec.mjs` |
| Every `SHELL` entry is cached after SW install | `service worker caches the full SHELL` | `tests/e2e/devhost.spec.mjs` |
| A sent message streams the canned reply | `streams a reply` | `tests/e2e/devhost.spec.mjs` |
| Self-host stack serves a chat round-trip through Postgres | `chat round-trip` | `tests/e2e/selfhost.spec.mjs` |
| Conversation survives an `app` restart | `persists across restart` | `tests/e2e/selfhost.spec.mjs` |
| Post-deploy: public paths 200, auth wall holds, email-in exclusion live | `infra/smoke-test.sh` exit code | `.github/workflows/deploy.yml` job `smoke` |
| Smoke's public paths ⊆ Bicep `excludedPaths` | `SmokePublicPaths_AreExcludedFromEasyAuth` | `tests/Agent42.Tests/E2eWorkflowTests.cs` |
| CI jobs and gate are wired and have a measuring tool | `EveryGate_HasAMeasuringToolInThisRepo`, `E2eWorkflow*` | `QualityGateTests.cs`, `E2eWorkflowTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

- `coverage_new_code_pct_min`: 80 (new C# is the guard test only; Node/YAML/bash are not measured by coverlet)
- `cognitive_complexity_max`: 15 (S3776) · `method_lines_max`: 80 (S138) · `method_params_max`: 11 (S107)
- `dependencies.vulnerable_packages`: 0 (also run `npm audit --omit=dev` for `tests/e2e`, report only)
- NEW: `e2e_failing_tests_max: 0`, measured by the Playwright run (`tests/e2e/playwright.config.mjs`)

## Out of scope

Real-LLM or Cosmos tests in CI; authenticated post-deploy flows or a CI Entra identity; automatic rollback on smoke failure;
Firefox/WebKit; visual regression; Android; the `compose.azure.yaml` Entra variant; feature-level browser tests beyond the three smokes.

## Review

Critic verdict: not clean on first pass (1 high, 4 medium); no ADR contradiction; ADR 0151 free.

- HIGH `--no-launch-profile` wrong port/env: **accepted**, step 1 fixed.
- MED no deploy outputs / missing login for `smoke`: **accepted**, step 4 now captures `backendUrl` and adds its own login.
- MED `/api/email-in` is POST-only: **accepted**, step and context specify POST + accepted statuses; flag skips it too.
- MED wrong "repo-relative wwwroot" claim: **accepted**, corrected; `webServer.cwd` added.
- MED gate is nominal (tool is a config file, measurement is the job exit code): **partly rejected**. Playwright's exit status is the real
  measurement, same shape as `vulnerable_packages: 0` (a CI job result), and the gate yaml key must be numeric for `QualityGateTests`.
  The ADR (step 0) must state this explicitly rather than pretend it is a computed number; the `enforcement.ci` comment is extended.
- LOW SSRF risk: not a risk, spec text replaced with the evidence. `DeployTriggerTests` unaffected; the `infra/**` deploy trigger is noted.

## Divergences from the plan (as executed)

- **No `azure/login` in `smoke`.** The job only curls the `backend-url` job output from `deploy`, so it needs no credentials; the login step the
  Review asked for was unnecessary.
- **`/shared/themes.css`, not `/shared/`.** A real file under the excluded `/shared/*` proves more than the directory; `E2eWorkflowTests` pins it to the Bicep list.
- **No Playwright retries.** A retry would let a flaky SHELL or service-worker test pass on the second try and weaken the gate.
- **Compose project name `agent42-e2e`** (found live: the default project name collides with a developer's own self-host stack). Recorded as a gotcha.
- **`EasyAuthExclusionTests.IsExcluded` / `ExcludedPaths` widened to `internal`** and reused rather than copied.
- **`e2e-selfhost.yml` is path-filtered + nightly + manual**, as planned, so it is not a gate on every PR; `e2e-devhost` is.
- **Not run locally:** the `deploy.yml` `smoke` job and `e2e-selfhost.yml` themselves (only their scripts/stack); the first real runs are on the PR and the next deploy.
- 3 `LiveCopilotTests` fail locally (need credentials); unrelated.
