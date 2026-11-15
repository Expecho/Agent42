planned-against: main @ abfbf92

# Plan: make the Azure IaC reusable for other tenants

## Overview

`infra/main.bicep` and `.github/workflows/deploy.yml` deploy only to the owner's tenant. The Bicep takes an
existing ACR (`crexpechohosting`), an existing Container Apps environment (`cae-hosting` in `rg-hosting`) and
the owner's managed certificate, and it defaults to the owner's tenant id and Easy Auth client id. The workflow
also hardcodes the registry and resource group, and runs a role grant on the owner's Foundry account. The goal:
someone who forks the repo runs one bootstrap script, sets the GitHub secrets and variables it prints, and
pushes. Prod must deploy with **no functional resource changes** (the image tag changes every deploy, and
what-if reports noise modifies on the container app because of `@secure` params and Container App secrets).

## Affected areas (verified against the tree at abfbf92)

- `infra/main.bicep`: tenant-specific parameter defaults, and where the ACR and environment names come from.
- `infra/shared.bicep` (**new**): create-or-reuse for the ACR, the Log Analytics workspace and the Container
  Apps environment.
- `infra/modules/container-app.bicep`: new optional app-config env vars (see Step 4).
- `infra/bootstrap.sh` (**new**): a one-time Entra/RBAC setup that the operator runs themselves.
- `.github/workflows/deploy.yml`: `vars.*` instead of literals, a shared-infra step, a preflight step, and
  removal of the Foundry grant step.
- `.github/workflows/pr-checks.yml`: a `bicep build` job.
- `tests/Agent42.Tests/`: a new `InfraPortabilityTests.cs`. Existing infra-reading tests must stay green:
  `AppInsightsWiringTests`, `EasyAuthSessionTests`, `EasyAuthExclusionTests`, `CopilotFilesMountTests`,
  `DeployTriggerTests`, `AndroidReleaseTests`, `ChromeExtensionReleaseTests`, `LocalBridgeReleaseTests`,
  `QualityGateTests`, `ContextDevRemovalTests`.
- Docs: `docs/features/azure-deployment.md` (**new**), `docs/features/README.md`, `README.md` (deploy section
  ~line 313), `CLAUDE.md` (Manual prerequisites table), `.claude/skills/deploy/SKILL.md`, `.agentic/gotchas.md`
  (entries listed in Step 8), and the `.agentic/quality-gates.yaml` `enforcement.ci` **comment** (no gate change).
- ADR: a new `.agentic/decisions/0128-*.md` (via `/decide`). It amends 0121 (see Context).

## Context & constraints

**User decisions (interview, 2026-11-14):**
1. **Create-or-reuse shared infra.** With no existing names given, Bicep creates an ACR, a Log Analytics
   workspace and a Container Apps environment in the app's resource group. Prod passes its existing names and
   creates nothing new. The rejected alternative was bring-your-own plus docs.
2. **Prod's tenant-specific values live in GitHub repo variables (`vars.*`)**, not in a committed `.bicepparam`.
   This was the user's explicit choice over the recommended committed params file. Consequence: prod's
   ACR, environment, domain, tenant, client id, Cosmos region and OTLP endpoint are no longer visible in code.
   The ADR must record that this trades hard rule 2 ("code is the source of truth") for portability, and that
   the preflight guard (Step 3) is the compensation.
3. **Entra setup via a bootstrap script** that the operator runs. The deploy SP never gets Graph permissions.
   The rejected alternatives were the Graph Bicep extension (it needs tenant admin consent for
   Application.ReadWrite.OwnedBy) and docs only.
4. **Verification:** CI `bicep build`, plus a prod what-if with no changes, which **the user** runs because it
   needs the deploy secrets. A fresh-resource-group test deploy is **not** part of this plan.

**Investigation findings:**
- **An absent GitHub variable expands to an EMPTY string.** For `customDomainName`, empty means "bind no
  domain", so a missing `vars.CUSTOM_DOMAIN_NAME` would **silently unbind chat.agent42.nl** on prod. See the
  gotcha "Custom domain must stay declared in Bicep" (`.agentic/gotchas.md` ~1327) and the
  `customDomains: !empty(...)` branch in `infra/modules/container-app.bicep`. This is why Step 3's preflight
  guard is load-bearing, not a nicety. The same absent-secret-expands-to-empty trap is already documented on
  `searxngSecret`'s `@minLength` in `infra/main.bicep`.
- A missing `COSMOS_LOCATION` would try to move the existing northeurope account. That fails loudly (the
  region is immutable, gotcha ~1321), but the preflight should still catch it with a clear message.
- The Foundry grant step in `deploy.yml` is **dead**. ADR 0017 and the gotcha "A cross-RG role grant in RG
  `agent42`…" (~1333) say "safe to remove", and its scope is owner-only. Remove it, and rewrite that gotcha so
  the durable "cross-RG grant must be a CLI step" pattern survives without the live example.
- `build-and-push` pushes to the ACR **before** the Bicep deploy. In create mode the ACR must therefore exist
  first, so `shared.bicep` is deployed at the start of `build-and-push`, and its outputs (registry name, env
  name, env RG) feed both jobs.
- The ACR pull uses **admin credentials** (`az acr credential show` in deploy.yml, `registries` in
  container-app.bicep). A created ACR must set `adminUserEnabled: true` to keep that shape. Switching to
  managed-identity AcrPull is out of scope: system-assigned identity has a create-time chicken-and-egg.
- The environment storage links (`copilotEnvironmentStorage`, `signalEnvironmentStorage` in main.bicep) are
  scoped to `containerAppsEnvironmentResourceGroup`. In create mode that is the app RG, so it works unchanged.
- App config that is tenant-specific but **committed** in `src/Agent42.Backend/appsettings.json`:
  `Push:VapidPublicKey` + `Push:VapidSubject` (a fork's private key would not match the committed public key,
  which breaks push), `GitHub:OAuth:ClientId`, `Email:InboundDomain` and `Access:Owners`/`Identity:Aliases`.
  All of them read IConfiguration, so env vars override them. Owner object ids are tenant-scoped, so the
  owner's ids never match in another tenant (harmless), but a fork has no way to name its own owners.
  ADR 0121 says the owner list is **committed**. Allowing a deploy-time override is an amendment to 0121, and
  ADR 0128 records it. Prod keeps the committed list and sets no override.
- `Access__Owners__0` overrides **index 0 only**. If the committed list is longer, its tail survives, which is
  harmless across tenants. The ADR records this, and the docs tell a fork to clear the list in its own
  appsettings if it wants exactly its own set.
- Easy Auth's redirect URI is `https://<host>/.auth/login/aad/callback`, and the host is unknown before the
  first deploy. So `bootstrap.sh` is **two-phase**: `init`, then `redirect <host>` after the first deploy.
- **Hard rule 5:** the executor writes `bootstrap.sh` but **never runs it** against Azure. It creates role
  assignments. The executor verifies it only with `bash -n` and `shellcheck` (if installed).
- Bicep CLI available locally: `az bicep version` reports 0.44.1. No CI job builds Bicep today.
- `deploy.yml`'s `appInsightsConnectionString="${{ secrets.APPLICATIONINSIGHTS_CONNECTION_STRING }}"` line is
  pinned verbatim by `AppInsightsWiringTests`. Keep it byte-identical.

## Steps

### Step 1 — Record the decision (`/decide`)
Write ADR 0128, "Azure IaC is tenant-portable: create-or-reuse shared infra, repo variables, bootstrap
script". It covers decisions 1–3 above, the rejected alternatives, the hard-rule-2 trade-off and its
compensating preflight, the 0121 amendment (deploy-time owner override, with index-0 semantics), and the
removal of the Foundry grant. In the same commit, add an "Amended by 0128" line to 0121.
Also add the 0128 row to `.agentic/decisions/README.md`.
**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~DocPath|FullyQualifiedName~Adr"` (the executor
first runs `grep -rln "doc-path\|DocPath" tests/` to confirm the real class name, and records it), then
`git diff --stat` shows only `.agentic/` files.

### Step 2 — `infra/shared.bicep` + generic `main.bicep` defaults
- New `infra/shared.bicep` (`targetScope = 'resourceGroup'`) takes `existingRegistryName`,
  `existingEnvironmentName` and `existingEnvironmentResourceGroup` (all default `''`), plus `location` and `tags`.
  - If the registry name is empty, it creates `Microsoft.ContainerRegistry/registries` named
    `cr${uniqueString(resourceGroup().id)}`, with SKU Basic and `adminUserEnabled: true`.
  - If the environment name is empty, it creates a Log Analytics workspace and a
    `Microsoft.App/managedEnvironments` (Consumption) wired to it.
  - Outputs: `registryName`, `environmentName`, `environmentResourceGroup`. Reused values pass straight through.
  - Use the API versions main.bicep/modules already use where they exist. Resolve new resource types' API
    versions with `az provider show` or the Bicep linter, never from memory (hard rule 4 in spirit).
- `infra/main.bicep`:
  - Remove the owner-specific defaults. `containerRegistryName`, `containerAppsEnvironmentName`,
    `containerAppsEnvironmentResourceGroup` and `authClientId` become **required** (no default; the workflow
    passes the shared.bicep outputs).
  - `customDomainName`, `customDomainCertificateName` and `otlpEndpoint` default to `''`.
  - `azureOpenAIEndpoint` default becomes `''` too, **but** `container-app.bicep` emits `AzureOpenAI__Endpoint`
    unconditionally, so prod would flip from its current value to empty (a what-if modify). Prod therefore
    passes it via `vars.AZURE_OPENAI_ENDPOINT` (listed in Step 8's prod values). The value is dormant (ADR 0019)
    but must stay byte-equal on prod.
  - `azureOpenAITenantId` defaults to `tenant().tenantId`. Safe only if prod's tenant is `00000000-…`; Step 9's
    what-if proves it, so say so there.
  - Update the deploy comment at the top of the file.
  - **Scrub `@description('…')` strings and comments of owner literals** (`chat.agent42.nl`, `rg-hosting`,
    `cae-hosting`, `rg-agent42`, `crexpechohosting`, `agent42` Foundry names). They are string literals, not
    comments, so Step 7's test sees them. Known sites at abfbf92: `infra/main.bicep` lines 4, 44, 59, 162, 164-165
    and `infra/modules/container-app.bicep` line 112. Re-grep after editing.
  - Add a `@description` note on the custom-domain pair saying both or neither.
  - Optionally add a Bicep `fail()`/assert that the cert is required when a domain is set, if the local Bicep
    version supports it. Otherwise the check goes in preflight.
- **Verify:** `az bicep build --file infra/main.bicep --stdout > /dev/null` and the same for `infra/shared.bicep`
  (no errors), then `dotnet test --filter "FullyQualifiedName~EasyAuth|FullyQualifiedName~CopilotFilesMount|FullyQualifiedName~AppInsightsWiring"`.

### Step 3 — `deploy.yml`: variables, shared step, preflight, drop the Foundry grant
- Delete the `env.REGISTRY_NAME` and `env.RESOURCE_GROUP` literals. Keep the image names. The resource group
  comes from `vars.AZURE_RESOURCE_GROUP`.
- Add a **`preflight` job FIRST**; both `build-and-push` and `deploy` `needs: preflight`. It must run before
  `shared.bicep`, because in create mode an empty/mistyped variable would otherwise create a stray ACR,
  workspace and environment in prod's RG and push images to the new ACR before anything could fail. It logs in
  to Azure and fails the run with a clear message, naming the variable to set, when:
  - (a) `AZURE_RESOURCE_GROUP` or `AUTH_CLIENT_ID` is empty;
  - (a2) the three shared-infra variables `ACR_NAME`, `CAE_NAME`, `CAE_RESOURCE_GROUP` are not **all set or all
    empty**. Create mode must be an explicit choice, never a side effect of one missing variable;
  - (b) exactly one of `CUSTOM_DOMAIN_NAME` / `CUSTOM_DOMAIN_CERTIFICATE_NAME` is set;
  - (c) **the existing container app has a custom domain bound that is not `vars.CUSTOM_DOMAIN_NAME`**. Read it
    with `az resource show` on `Microsoft.App/containerApps`, query
    `properties.configuration.ingress.customDomains[].name`, and treat a missing app as an empty list;
  - (c2) the existing container app's `properties.managedEnvironmentId` does not match the environment the
    variables resolve to (an app cannot move between environments);
  - (d) the Cosmos account already exists in a region other than `vars.COSMOS_LOCATION` (or the RG location
    when unset). Name the variable to set;
  - (e) `ACCESS_OWNERS` contains `local-dev`.
- After preflight, `build-and-push` starts by deploying `az deployment group create -f infra/shared.bicep` with
  `existingRegistryName=${{ vars.ACR_NAME }}`, `existingEnvironmentName=${{ vars.CAE_NAME }}` and
  `existingEnvironmentResourceGroup=${{ vars.CAE_RESOURCE_GROUP }}`. Expose the outputs as job outputs. The
  ACR login, build and push then use the output registry name.
- Bicep deploy: pass `containerRegistryName`, `containerAppsEnvironmentName` and
  `containerAppsEnvironmentResourceGroup` from the build job's outputs. Pass `authClientId`, `customDomainName`,
  `customDomainCertificateName`, `cosmosLocation` (only when set: build the args array conditionally, so an
  unset value falls back to the Bicep default) and `otlpEndpoint` from `vars`. Pass the Step 4 app-config
  variables the same way. The existing `secrets.*` lines stay unchanged, and the AppInsights line stays
  **byte-identical**.
- Delete the "Grant Cognitive Services OpenAI User" step and its comment.
- Keep the `on.push.paths` list unchanged (`DeployTriggerTests`). `infra/**` already covers the new files.
- **Verify:** `dotnet test --filter "FullyQualifiedName~DeployTrigger|FullyQualifiedName~AppInsightsWiring|FullyQualifiedName~ReleaseTests"`, then `actionlint` if available. Otherwise note it as SKIPPED.

### Step 4 — Optional app-config overrides through Bicep
- New `main.bicep` + `container-app.bicep` params, all with a default of `''`: `vapidPublicKey`, `vapidSubject`,
  `githubOAuthClientId`, `emailInboundDomain`, plus `accessOwners` (an array, default `[]`).
- Each one emits its env var **only when non-empty**: `Push__VapidPublicKey`, `Push__VapidSubject`,
  `GitHub__OAuth__ClientId`, `Email__InboundDomain`, and `Access__Owners__<i>` for each entry. Copy the
  existing `empty(...) ? [] : [...]` concat shape exactly.
- In `deploy.yml`, pass these from `vars.VAPID_PUBLIC_KEY`, `vars.VAPID_SUBJECT`, `vars.GH_OAUTH_CLIENT_ID`
  (**not** `GITHUB_*`: GitHub reserves that prefix for variable and secret names, which is why the secret is
  already `GH_OAUTH_CLIENT_SECRET`), `vars.EMAIL_INBOUND_DOMAIN` and `vars.ACCESS_OWNERS` (comma-separated, split into a JSON array in bash).
- Prod sets none of them, so the committed appsettings stay authoritative for prod (ADR 0121 still holds there).
- **Never** accept `local-dev` in `ACCESS_OWNERS` (ADR 0062/0121). The preflight rejects it.
- **Verify:** `az bicep build`, then the new `InfraPortabilityTests` rows (Step 7).

### Step 5 — `infra/bootstrap.sh` (operator-run, never executed by the agent)
- `bootstrap.sh init --resource-group <rg> --location <loc> [--app-name agent42]`, idempotent where `az`
  allows:
  - creates the RG;
  - registers the resource providers the deploy needs (`Microsoft.App`, `Microsoft.ContainerRegistry`,
    `Microsoft.OperationalInsights`, `Microsoft.DocumentDB`, `Microsoft.Storage`, `Microsoft.Authorization`),
    because the RG-scoped deploy SP cannot, and a fresh subscription fails the first deploy confusingly;
  - creates the Entra app registration for Easy Auth (sign-in audience: single tenant, **web** platform,
    **ID-token issuance enabled**: without it the first sign-in fails), its service principal with
    `appRoleAssignmentRequired=true`, and a client secret;
  - creates the deploy SP (client secret, matching the existing gotcha "Deploy service principal uses a
    client secret") with **Contributor + Role Based Access Control Administrator on the RG**, which is the
    elevation the attachments-RBAC gotcha (~1463) relies on;
  - generates a random `SEARXNG_SECRET`;
  - prints a block of **GitHub secrets** (`AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_SUBSCRIPTION_ID`,
    `AZURE_TENANT_ID`, `AAD_CLIENT_SECRET`, `SEARXNG_SECRET`) and a block of **GitHub variables**
    (`AZURE_RESOURCE_GROUP`, `AUTH_CLIENT_ID`), as `gh secret set` / `gh variable set` commands the operator
    can paste.
  - It also prints a note that it does **not** assign users, so the operator must assign themselves to the
    enterprise app.
- `bootstrap.sh redirect --app-id <authClientId> --host <fqdn-or-custom-domain>` adds
  `https://<host>/.auth/login/aad/callback` to the app registration's web redirect URIs, without removing
  existing ones.
- Header comment: says who runs it (the operator), the permissions it needs (Owner, or User Access
  Administrator + Contributor, on the subscription, plus the right to create app registrations), and why it
  is not in CI (no Graph permissions for the deploy SP, ADR 0128).
- Use the MSYS `/`-scope trap fix from the gotcha (~1477) for `--scope` arguments. No `.gitattributes` exists
  at abfbf92, so **add one** with `*.sh text eol=lf` (a CRLF checkout breaks the script on Linux/macOS).
- **Verify:** `bash -n infra/bootstrap.sh`, and `shellcheck infra/bootstrap.sh` if installed (otherwise SKIPPED).
  **Do not run the script.**

### Step 6 — CI: `bicep build` job (parallelizable with Step 5)
- Add a `bicep` job to `.github/workflows/pr-checks.yml`: `az bicep install`, then `az bicep build --file`
  for `infra/main.bicep` and `infra/shared.bicep`. A non-zero exit fails the PR. No Azure login is needed.
- Update the workflow's header comment and the `enforcement.ci` comment in `.agentic/quality-gates.yaml` to
  list "bicep build". That is a **comment-only** edit. It adds no gate or threshold, so ADR 0050's admission
  test does not apply. Make sure `QualityGateTests` still passes.
- **Verify:** `dotnet test --filter "FullyQualifiedName~QualityGate"`, and the job runs green on the MR.

### Step 7 — `InfraPortabilityTests` (written alongside Steps 2–6, listed here for mapping)
New `tests/Agent42.Tests/InfraPortabilityTests.cs`, copying the file-reading shape of `EasyAuthSessionTests`
(repo-root discovery plus comment stripping). It asserts:
- No owner-specific literal in `infra/**/*.bicep` or `.github/workflows/deploy.yml`, **including inside
  `@description('…')` strings** (only `//` and `/* */` comments are exempt). The comment stripper must not
  truncate string literals such as `'https://…'`: reuse `EasyAuthSessionTests`' helper only after checking it
  is string-aware, otherwise write a small string-aware one. Literals checked:
  `crexpechohosting`, `cae-hosting`, `rg-hosting`, `rg-agent42`, `agent42.nl`, `00000000-`, `00000000-`,
  `peterbons`, `resourceGroups/agent42`.
- `main.bicep` declares `containerRegistryName`, `containerAppsEnvironmentName` and `authClientId` with **no**
  default.
- `shared.bicep` creates the ACR conditionally with `adminUserEnabled: true`, and creates the environment
  conditionally.
- `deploy.yml` deploys `infra/shared.bicep` before any `az acr login`.
- `deploy.yml` contains the preflight step, and it references `customDomains`.
- `deploy.yml` contains no `roleAssignments` PUT.
- Every `vars.X` that `deploy.yml` references appears in `docs/features/azure-deployment.md`, and every
  `secrets.X` appears there too (a consistency check, so the docs can't drift from the workflow).
- Every secret and variable that `bootstrap.sh` prints is one `deploy.yml` references.
- The reverse direction too (hard-rule-2 compensation): every variable named in the docs' "this repo's prod"
  table is referenced by `deploy.yml`.
- `deploy.yml` has a `preflight` job and both other jobs `needs: preflight`; the preflight text references
  `managedEnvironmentId` and `local-dev`.
- The Step 4 env vars are each emitted only under an `empty(...)` guard. Copy the shape of
  `AppInsightsWiringTests`' gating assertion.
- **Verify:** `dotnet test --filter "FullyQualifiedName~InfraPortability"`.

### Step 8 — Docs and knowledge (`/sync-docs`)
- New `docs/features/azure-deployment.md` covers:
  - the fork flow: bootstrap init → set secrets and vars → push → bootstrap redirect → assign users;
  - a table of every variable and secret (required or optional, with the effect when missing);
  - the custom-domain procedure (bind once with the CLI, then set both variables);
  - the app-config overrides, including the VAPID key-pair generation and the owners index-0 note;
  - and, for **this repo's prod**, the exact variable values to set before merging. Those values are not secret
    and are copied from the current `main.bicep`/`deploy.yml` defaults: `ACR_NAME=crexpechohosting`,
    `CAE_NAME=cae-hosting`, `CAE_RESOURCE_GROUP=rg-hosting`, `AZURE_RESOURCE_GROUP=rg-agent42`,
    `AUTH_CLIENT_ID=<auth-client-id>`, `CUSTOM_DOMAIN_NAME=chat.agent42.nl`,
    `CUSTOM_DOMAIN_CERTIFICATE_NAME=<certificate-name>`, `COSMOS_LOCATION=northeurope`,
    `OTLP_ENDPOINT=http://otlp.example.com:4317`,
    `AZURE_OPENAI_ENDPOINT=https://agent42.services.ai.azure.com`.

    Before setting these, the executor double-checks each value against the
    pre-change files with `git show abfbf92:infra/main.bicep`.

    Note: listing them in docs keeps a code-side record of prod's values, which partly offsets the
    hard-rule-2 trade-off.
- Add a link in `docs/features/README.md`. Update `README.md`'s deploy section. Update `CLAUDE.md`'s "Project
  facts" Deploy and AI Foundry bullets (they hardcode `crexpechohosting`, `rg-agent42` and the Foundry grant
  story; "the deploy SP holds only role-assignment rights" is stale once the grant is removed). In `CLAUDE.md`'s Manual
  prerequisites, add a row for the required repo variables (`AZURE_RESOURCE_GROUP`, `AUTH_CLIENT_ID`;
  missing means the deploy fails preflight), and add ADR 0128 to the feature map's Layout row or a new
  "Deployment" row.
- `.claude/skills/deploy/SKILL.md`: the registry and resource group now come from repo variables.
- `.agentic/gotchas.md`:
  - rewrite "Custom domain must stay declared" (now enforced by the preflight, and set via vars) and
    "Cosmos account region is pinned…" (now `vars.COSMOS_LOCATION`);
  - rewrite the cross-RG Foundry grant entry (the grant is removed, the pattern stays);
  - point "Attachments Storage RBAC…" at `bootstrap.sh`;
  - add a new entry: "An absent GitHub variable expands to empty, and empty means 'off' for optional Bicep
    params", with `deploy.yml` (preflight) as the evidence.
- Update `.agentic/specs/` lifecycle per `/execute-plan`.
- **Verify:** `dotnet test Agent42.slnx` (the doc-path integrity tests), then `dotnet format Agent42.slnx --verify-no-changes`.

### Step 9 — Hand-off checks (the USER runs these; record the results in the MR)
1. Set the prod repo variables from Step 8 **before merging**. Without them the preflight fails, which is the
   intended fail-closed.
2. Run a prod what-if for `shared.bicep`, then `main.bicep`, with the prod values and the real secrets
   (exact commands go in the MR description). Expected: `shared.bicep` creates nothing, and `main.bicep` shows
   no create, delete or modify beyond the image tag. Secret values are opaque in what-if, which is acceptable.
3. After merging, watch the Deploy run. Confirm chat.agent42.nl still serves and signs in.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proven by | File |
|---|---|---|---|
| 1 | No owner-specific literal remains in infra Bicep or deploy.yml code | `InfraPortabilityTests.NoOwnerLiterals` | `tests/Agent42.Tests/InfraPortabilityTests.cs` |
| 2 | ACR and environment are created when no existing names are given, and reused otherwise | `InfraPortabilityTests.SharedInfraIsCreateOrReuse` + CI `bicep build` | same + `.github/workflows/pr-checks.yml` |
| 3 | Shared infra is deployed before the image push | `InfraPortabilityTests.SharedDeployPrecedesAcrLogin` | same |
| 4 | A missing domain variable cannot silently unbind a bound domain | `InfraPortabilityTests.PreflightGuardsCustomDomain` (structure) + Step 9.2 what-if (behavior) | same + MR |
| 5 | The Foundry role grant is gone | `InfraPortabilityTests.NoRoleAssignmentPut` | same |
| 6 | App-config overrides are emitted only when set | `InfraPortabilityTests.AppConfigOverridesAreGated` | same |
| 7 | The docs list every variable and secret the workflow reads, and the bootstrap prints only those | `InfraPortabilityTests.DocsCoverWorkflowInputs`, `BootstrapPrintsOnlyWorkflowInputs` | same |
| 8 | Templates compile | CI `bicep` job; local `az bicep build` | `.github/workflows/pr-checks.yml` |
| 9 | Existing infra pins unchanged (Easy Auth exclusions, session, mounts, AppInsights line, deploy paths) | existing `EasyAuth*`, `CopilotFilesMountTests`, `AppInsightsWiringTests`, `DeployTriggerTests` | existing |
| 10 | Prod deploys with no functional resource change (no create/delete; modifies limited to the image tag and secret-driven what-if noise) | Step 9.2 what-if (run by the user) | MR description |
| 11 | `bootstrap.sh` is syntactically valid | `bash -n` (+ `shellcheck` if present) | n/a (command) |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at abfbf92)

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (expect little or no new C# outside tests, so likely N/A) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` |
| `vulnerable_packages` | 0 | CI package-freshness job |
| Build / tests / format | must pass | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` |

## Out of scope

- A fresh-resource-group or fresh-tenant test deploy. The user declined it, so "works for another tenant" is
  proven structurally, not end to end. Say so in the MR.
- OIDC federated credentials for the deploy SP (it stays on a client secret).
- Managed-identity AcrPull instead of ACR admin credentials.
- Creating the managed certificate or custom domain in Bicep. Binding stays a one-time CLI step.
- Owner-specific content outside the IaC: `wwwroot/legal/*.html` (operator name and domain), the Android
  `DEFAULT_URL`, the Chrome extension's fallback server, and the committed appsettings values (prod keeps them;
  forks override via Step 4 or their own appsettings).
- The Graph Bicep extension for the app registration.
- Any change to the self-host path (ADR 0127).

## Review

Critic verdict: **not approvable as first drafted**; all findings were legitimate and are fixed above. No
contradiction with ADRs 0042, 0050, 0107, 0111, 0127. The 0121 amendment is recorded in ADR 0128.

| # | Finding | Handling |
|---|---|---|
| 1 | `vars.GITHUB_OAUTH_CLIENT_ID` is an illegal name (reserved `GITHUB_` prefix) | Fixed: `GH_OAUTH_CLIENT_ID` (Step 4) |
| 2 | Preflight ran after shared infra, so a missing prod variable could create stray ACR/environment in prod and push images | Fixed: separate first `preflight` job, all-or-none check on the three shared-infra variables, `managedEnvironmentId` check (Step 3) |
| 3 | `azureOpenAIEndpoint` default `''` flips prod's env var | Fixed: `vars.AZURE_OPENAI_ENDPOINT` and a Step 8 prod value; tenant default flagged as proven only by the what-if (Step 2) |
| 4 | Owner literals live inside `@description` strings, and a naive comment stripper truncates `https://` | Fixed: explicit scrub step with known sites; string-aware stripper required in the test (Steps 2, 7) |
| 5 | Resource providers not registered on a fresh subscription | Fixed in bootstrap init (Step 5) |
| 6 | App registration needs ID-token issuance and the web platform | Fixed in Step 5 |
| 7 | "Zero resource changes" overstated for what-if | Fixed: reworded in Overview and AC 10 |
| 8 | Hard-rule-2 compensation one-directional | Fixed: reverse docs-to-workflow test (Step 7) |
| 9 | Step 1 verify unnamed; CLAUDE.md Project facts, decisions README and `.gitattributes` missing | Fixed (Steps 1, 5, 8) |

Rejected: none.

## Divergences from the plan (implementation, 2026-11-14)

- **ADR 0128 amends 0017 as well as 0121** (an "Amended by" note on each); the decisions README has no per-ADR table, so no row was added.
- **ACR API version `2025-11-01`**: `2026-11-01` has no local Bicep types (BCP081).
- **Preflight (c2) also runs in create mode**: an existing app outside `AZURE_RESOURCE_GROUP` with all three shared-infra variables empty fails (critic finding: lost variables would otherwise create a stray ACR/environment).
- **Test names** differ from the AC table (`Deploy_DeploysTheSharedInfraBeforeTheFirstAcrLogin`, `Docs_ListEveryVariableAndSecretTheWorkflowReads`, ...); same assertions. The Bicep comment stripper is regex-based and string-aware.
- **Not preflighted, left to the Step 9.2 what-if:** `OTLP_ENDPOINT`, `AZURE_OPENAI_ENDPOINT`, and the `tenant().tenantId` default.
- **Skipped tools:** `shellcheck`, `actionlint` and `jq` are not installed locally; run blocks were checked with `bash -n`.
- **Known unrelated failures locally:** DevHost build under SDK 11; `LiveCopilotTests` is flaky (passed on rerun).
