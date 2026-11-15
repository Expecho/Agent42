---
id: 0130
date: 2026-11-15
status: accepted
tags: [infra, deploy, github-actions, self-hosting, containers]
supersedes: []
extends: [0127, 0128]
amends: []
---

# Publish multi-arch images to GHCR, so self-hosting and Azure need no build

## Context
Self-hosting (ADR 0127) made people clone the repo and run `docker compose up --build`, which needs a long first build, and
the Azure path (ADR 0128) made them fork the repo and let GitHub Actions build and push to their own registry. Neither is
needed to just run the app: the images hold no configuration (everything is an environment variable), so one image runs in
any tenant or on any machine.

## Decision
1. **`publish-images.yml` pushes two public images to GHCR**, `ghcr.io/<owner>/agent42-backend` and
   `agent42-searxng`, on every push to `main` (tags `latest`, `sha-<short>`) and every `v*` tag (`1.2.3`, `1.2`, `1`).
   Each architecture (amd64, arm64) builds on its own native runner, pushed by digest, and a merge job writes the manifest.
   The owner is lowercased from `GITHUB_REPOSITORY_OWNER`, never typed, so a fork publishes to its own namespace.
2. **`compose.yaml` pulls these images** (`${AGENT42_REGISTRY}/agent42-*:${AGENT42_VERSION}`, defaults `ghcr.io/expecho` and
   `latest`). Building from source moves to `compose.build.yaml`, added with `-f compose.yaml -f compose.build.yaml`.
3. **Azure deployers import the same images** into their registry with `az acr import` and deploy `infra/main.bicep` by hand
   (`docs/setup/azure.md`). The image names match the Bicep defaults, so nothing in the IaC changes.
4. **Production is untouched.** `deploy.yml` still builds and pushes its own images to the Azure registry, tagged by commit.

## Alternatives considered
- **Keep one `compose.yaml` with both `image:` and `build:`.** `docker compose up` builds when the image is missing locally
  rather than pulling, so the default would stay slow. Two files make each mode explicit.
- **Build arm64 under QEMU in one job.** A .NET restore and publish under emulation takes far too long and crashes
  intermittently; native arm runners are free for public repositories.
- **Make `deploy.yml` import from GHCR instead of building (an "image source" variable).** Convenient for forks, but it
  edits the production deploy path for a case the manual import already covers. Left for later if forks ask.
- **Docker Hub.** Needs a separate account and secret; GHCR authenticates with the built-in `GITHUB_TOKEN`.

## Consequences
- The package is **private on first push**; the owner makes it public once (Package settings, Change visibility), or
  anonymous `docker pull` and `az acr import` fail. A workflow cannot do this.
- `latest` follows `main`, which is unreleased by definition. Pin `AGENT42_VERSION` to a tag or `sha-<commit>` for stability.
- A Container App rolls a revision only when its image reference changes, so an Azure deployer must pin a tag, not re-import
  `latest`.
- Images are public, so nothing tenant-specific may ever be baked in (the Dockerfiles bake only the signal-cli pin).
- The workflow can only be proven by running it; the first run on `main` is the real test.
