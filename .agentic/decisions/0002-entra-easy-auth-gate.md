---
id: 0002
date: 2026-07-05
status: accepted
tags: [auth, azure, container-apps]
---

# Gate the Container App behind Entra ID Easy Auth instead of app-level auth

## Context
The API was fully open while being publicly reachable (chat.agent42.nl), exposing
LLM spend and private conversations. Needed: sign-in for exactly two people with
minimal app code (commit 57790a1, 2026-07-05).

## Decision
Use Container Apps built-in authentication (Easy Auth) with Entra ID. Access is
restricted twice: assignment-required on the `agent42-easyauth` enterprise app AND
an `allowedPrincipals` allowlist in the auth config. The app itself only reads
`X-MS-CLIENT-PRINCIPAL-ID` (`CurrentUser.cs`) — the ingress strips inbound
`X-MS-CLIENT-*` headers, so the value is trustworthy.

## Alternatives considered
- **ASP.NET Core OIDC middleware in-app** — more code and config to maintain for a
  two-user personal app; Easy Auth gives the same gate declaratively in Bicep.
- **No auth / obscurity** — unacceptable: public endpoint burning Azure OpenAI quota.

## Consequences
- Every endpoint (UI + API) is protected at the ingress; no auth code in handlers.
- Local dev has no Easy Auth, so all requests map to the `local-dev` pseudo-user.
- The deploy workflow must keep passing `AAD_CLIENT_SECRET`; rotating that app
  registration secret means updating the GitHub secret too.
