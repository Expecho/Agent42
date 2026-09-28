---
name: deploy
description: Deploy Agent42 to Azure — explain, trigger, or monitor the GitHub Actions "Deploy" workflow (build → ACR → Bicep to rg-agent42).
user-invocable: true
argument-hint: "[status | trigger | watch]"
---

# Deploy Agent42

Deployment is fully owned by `.github/workflows/deploy.yml` (workflow name: **Deploy**).
There is no manual deploy path — never run `az deployment group create` or
`docker push` by hand against production.

## How it deploys

1. **Trigger:** push to `main` touching `src/**`, `infra/**`, `Directory.Packages.props`,
   `.dockerignore`, or the workflow file — or a manual `workflow_dispatch`. Merging a PR into
   `main` is the normal deploy, but a PR touching none of those paths does NOT deploy.
2. **build-and-push:** builds `src/Agent42.Backend/Dockerfile`, tags with the first
   12 chars of the SHA, pushes to ACR `crexpechohosting`.
3. **deploy:** `az deployment group create` of `infra/main.bicep` into RG `rg-agent42`
   (Cosmos pinned to `northeurope`), then an idempotent `az rest` step grants the
   container app identity `Cognitive Services OpenAI User` on the AI Foundry account
   in RG `agent42`. Both quirks are documented in `.agentic/gotchas.md` — read those
   entries before changing anything in this pipeline.

## Commands

| Intent | Command |
|--------|---------|
| Status of latest deploy | `gh run list --workflow Deploy --limit 5` |
| Trigger manually | `gh workflow run Deploy` (then `gh run watch`) |
| Watch a running deploy | `gh run watch` |
| Logs of a failed deploy | `gh run view <run-id> --log-failed` |

## Guardrails

- IAM/RBAC failures in the deploy: per CLAUDE.md hard rule 5, never fix by running
  role-assignment writes yourself — prepare the exact command and hand it to the user.
- If the deploy unbinds chat.agent42.nl, the managed certificate name drifted — see
  the "Custom domain must stay declared in Bicep" gotcha.
