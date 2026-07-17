---
id: 0002
date: 2026-07-05
status: accepted
tags: [auth, azure, container-apps]
---

# Gate the Container App behind Entra ID Easy Auth instead of app-level auth

> **Amended by [ADR 0022](0022-per-agent-api-key-and-async-run-endpoint.md) (2026-07-17) — one
> carve-out.** `/api/agent-run` is on `globalValidation.excludedPaths`, so it is NOT ingress-gated
> and DOES self-enforce auth in the handler (a per-agent API key, fail-closed 401). Every other
> endpoint (all UI + API) stays fully Easy-Auth-gated; this is the single hole. So the "no auth code
> in handlers" consequence below holds everywhere except that one key-gated path.

## Context
The API was fully open while being publicly reachable (chat.agent42.nl), exposing
LLM spend and private conversations. Needed: sign-in for exactly two people with
minimal app code (commit 57790a1, 2026-07-05).

## Decision
Use Container Apps built-in authentication (Easy Auth) with Entra ID. Access is
restricted twice: assignment-required on the `agent42-easyauth` enterprise app AND
an `allowedPrincipals` allowlist in the auth config. The app reads the injected
`X-MS-CLIENT-*` headers in `CurrentUser.cs` — `X-MS-CLIENT-PRINCIPAL-ID` for grain
keying, and additionally `-NAME`, `-IDP` and the `X-MS-CLIENT-PRINCIPAL` claims blob
for telemetry enrichment (ADR 0009) — the ingress strips inbound `X-MS-CLIENT-*`
headers, so the values are trustworthy.

## Alternatives considered
- **ASP.NET Core OIDC middleware in-app** — more code and config to maintain for a
  two-user personal app; Easy Auth gives the same gate declaratively in Bicep.
- **No auth / obscurity** — unacceptable: public endpoint burning Azure OpenAI quota.

## Consequences
- Every endpoint (UI + API) is protected at the ingress; no auth code in handlers — with the single
  exception of the ADR 0022 key-gated `/api/agent-run` (excluded from the ingress gate, self-enforced).
- Local dev has no Easy Auth, so all requests map to the `local-dev` pseudo-user.
- The deploy workflow must keep passing `AAD_CLIENT_SECRET`; rotating that app
  registration secret means updating the GitHub secret too.
