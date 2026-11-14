---
id: 0128
date: 2026-11-14
status: accepted
tags: [infra, deploy, bicep, github-actions, portability, auth, risk-accepted]
supersedes: []
extends: [0127]
amends: [0121, 0017]
---

# Azure IaC is tenant-portable: create-or-reuse shared infra, repo variables, a bootstrap script

## Context
`infra/main.bicep` and `deploy.yml` only deployed to the owner's tenant: an existing ACR, an existing Container Apps
environment and managed certificate, the owner's tenant id and Easy Auth client id as parameter defaults, and a
role grant on the owner's Foundry account. ADR 0127 made the app self-hostable without Azure; the goal here is that a
fork can also deploy to **its own Azure subscription** with one bootstrap script, a handful of GitHub secrets and
variables, and a push. Prod must keep deploying with no functional resource change.

## Decision
1. **Create-or-reuse shared infra.** `infra/shared.bicep` creates an ACR (Basic, admin user on, the pull shape the
   Container App already uses), a Log Analytics workspace and a Container Apps environment in the app's resource
   group when no existing names are given, and passes existing names straight through. Prod passes its names and
   creates nothing. `deploy.yml` deploys it at the start of `build-and-push`, because the image push needs the ACR.
2. **Tenant-specific values are GitHub repo variables (`vars.*`)**, not a committed `.bicepparam`. `main.bicep` has
   no owner-specific default left.
3. **A first `preflight` job fails closed** before anything is created or pushed. An absent variable expands to an
   empty string, and empty means "off" for optional params, so a missing `CUSTOM_DOMAIN_NAME` would silently unbind
   a live domain. Preflight checks the required variables, the all-or-none shared-infra trio, the domain pair and
   the domain currently bound, the environment the app already lives in, the Cosmos region, and `local-dev` in the
   owner list.
4. **Entra setup is an operator-run `infra/bootstrap.sh`** (two phases: `init`, then `redirect <host>` after the first
   deploy). The deploy SP never gets Graph permissions, and the agent never runs the script (hard rule 5).
5. **Optional app-config overrides** (`Push:VapidPublicKey`, `Push:VapidSubject`, `GitHub:OAuth:ClientId`,
   `Email:InboundDomain`, `Access:Owners`) are emitted as env vars only when non-empty. **This amends ADR 0121**, which
   says the owner list is committed: a deploy-time override is allowed. `Access__Owners__<i>` overrides by index, so an
   override replaces entries from index 0 and a longer committed list keeps its tail (harmless across tenants; a fork
   that wants exactly its own set clears the list in its own appsettings). Prod sets no override, so the committed
   list stays authoritative there. `local-dev` is still never an owner.
6. **The Foundry role grant step is removed.** Dead since ADR 0017 and owner-scoped. The pattern (a cross-RG grant
   must be a CLI step, not a nested module) stays in `.agentic/gotchas.md`.

## Alternatives considered
- **Bring-your-own ACR/environment plus docs.** Rejected by the user: every fork would hand-build shared infra.
- **A committed `.bicepparam` for prod.** Recommended, but rejected by the user in favour of repo variables.
- **The Graph Bicep extension for the app registration.** Needs tenant-admin consent for
  `Application.ReadWrite.OwnedBy` on the deploy SP.
- **Managed-identity AcrPull.** Out of scope: a system-assigned identity has a create-time chicken-and-egg.

## Consequences
- **Hard rule 2 ("code is the source of truth") is traded for portability** for prod's ACR, environment, domain,
  tenant, client id, Cosmos region and OTLP endpoint: they live in repo variables and are no longer visible in code.
  Compensations: the preflight guard, and `docs/features/azure-deployment.md` lists prod's values, pinned in both
  directions to the workflow by `InfraPortabilityTests`.
- The deploy SP gets **Role Based Access Control Administrator** on its resource group (needed for the storage role
  assignment), which can assign any role there, Owner included. A new default for forks; scope is the group only.
- `OTLP_ENDPOINT`, `AZURE_OPENAI_ENDPOINT` and the tenant default (`tenant().tenantId`) are not preflighted: an unset
  one modifies the app silently. The prod what-if is the gate for those.
- Prod's repo variables must be set **before** merging. Without them the preflight fails, which is the intended
  fail-closed.
- "Works for another tenant" is proven structurally (tests, `bicep build`), not by a fresh-tenant deploy.
- Out of scope: owner-specific content outside IaC (legal pages, Android `DEFAULT_URL`, the Chrome extension's fallback
  server, committed appsettings values).

## Evidence
`infra/shared.bicep`, `infra/main.bicep`, `infra/bootstrap.sh`, `.github/workflows/deploy.yml` (`preflight`),
`tests/Agent42.Tests/InfraPortabilityTests.cs`, `docs/features/azure-deployment.md`.
