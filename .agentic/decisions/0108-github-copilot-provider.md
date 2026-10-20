---
id: 0108
date: 2026-10-20
status: accepted
tags: [models, providers, copilot, github-copilot-sdk, github, oauth, egress, secrets, per-user, telemetry, trust-boundary, fleet, no-migration]
supersedes: []
extends: [0013, 0018]
amends: [0085, 0097]
---

# Copilot agents can run on the user's own GitHub Copilot subscription, through a "Sign in with GitHub" provider

## Context
On 2026-10-20 the user asked for GitHub Copilot as a model provider, like Anthropic (ADR 0103). In Agent42, Copilot is
the GitHub Copilot SDK's agent runtime, and it ran only on BYOK: ADR 0085 measured "BYOK without GitHub sign-in", and the
fleet (0097) copies that. GitHub has no supported public inference API for a Copilot plan. The pinned SDK (1.0.16) can
sign a single **session** in with a GitHub token (`SessionConfigBase.GitHubToken`), which the runtime uses "for content
exclusion, model routing, and quota checks". Spikes on 2026-10-20 ran on the repo's own builders, its SessionFs
provider and AF `GitHubCopilotAgent`, with a `gho_` token (output in
`.agentic/specs/completed/plan-copilot-github-account.md`, "Step 0 results"). They showed:
- the token lists the plan's models (21) and runs a custom-tool turn, and a bad token or no token fails closed;
- the token is never written to the transcript, and a session resumes after a runtime restart, but only with the token;
- a session moves between BYOK and token auth in both directions;
- with the built-in tools on, the shell's environment holds no token, and the runtime hardens git against credential
  prompts;
- egress went only to GitHub addresses.

## Decision
1. **A provider kind, `ProviderKind.GitHubCopilot`** (enum appended at the end, stored by name). It lives in the
   per-user overlay (ADR 0018) under the **reserved key** `github-copilot`, so its ids are `github-copilot:<model>`. The
   GitHub token is DP-encrypted into the existing `StoredProvider.ApiKeyCiphertext`, so there are no new persisted
   fields and the provider-key gotcha applies unchanged. It has no endpoint and no declared models, and a blank key
   means Skipped, like any keyless kind.
2. **One writer.** "Sign in with GitHub" is ADR 0025's app-driven web flow:
   - `GET /api/providers/github/start` and `/callback`, both behind Easy Auth;
   - one-time state and PKCE (S256) in a separate per-user pending grain (`github-oauth-pending`);
   - a plain `HttpClient` with no retry pipeline (rule 8);
   - the callback never reflects GitHub's body into the redirect.

   The generic `/api/providers` API refuses any write that names the kind **or** the reserved key, and so does the
   grain's `SaveAsync`; only `ConnectGitHubCopilotAsync` writes it. This stops a PUT from converting the provider into
   another kind that keeps the stored token as its key, and stops anyone from squatting the key. Disconnect is the
   ordinary DELETE.
3. **An operator GitHub OAuth App, not a GitHub App.** OAuth App user tokens (`gho_`) are the type the spike proved, and
   they don't expire, so there is **no refresh path** (contrast ADR 0025/0081).
   - **ADR 0019 holds.** The client secret is an operator *app identity*, not an operator *model*: it grants no model
     access by itself, and every turn runs on the signed-in user's own plan.
   - **Config.** `GitHub:OAuth:ClientId` is committed and `GitHub:OAuth:ClientSecret` comes from the
     `GH_OAUTH_CLIENT_SECRET` deploy secret (not `GITHUB_…`: GitHub Actions reserves that prefix for its own secret
     names). With either blank, the button is hidden, the start endpoint answers
     503, and the app boots.
   - **Two registrations.** An OAuth App has one callback URL, so dev and prod each need their own.
4. **Per session, never ambient.** Both `BuildSessionConfig`s (chat and fleet) set `GitHubToken`,
   `Provider = null` and **`EnableSessionTelemetry = false`** for this kind, and leave BYOK unchanged. The token never
   goes into `CopilotClientOptions` (one runtime serves every user) or into the runtime's `Environment`, and tests pin
   that `GH_TOKEN`, `GITHUB_TOKEN` and `COPILOT_GITHUB_TOKEN` can't reach it.
5. **Copilot agents only.**
   - **Refusing client.** The provider's chat client is `CopilotOnlyChatClient`, which refuses every request: the one
     server-side guard for plain chats, non-copilot stored agents, research, follow-ups and fleet chat agents.
   - **Fixed capabilities.** `GetCapabilities` returns tools-only with no cache read, so a copilot chat always builds
     the copilot composition.
   - **Never a default.** The model is never the default, and the provider is never the fallback provider that legacy
     unqualified ids resolve against.
   - **Fleet.** `IsToolCapable` excludes these models, the Copilot factory accepts them as `copilot/<id>`, and the
     planner prompt lists them separately.
   - **Pickers.** Every picker hides them except the agent editor for the copilot kind.
6. **Discovered live.** `CopilotModelDirectory` lists a user's plan through the **chat** runtime's client
   (`Rpc.Models.ListAsync(null, token)`; the fleet's client is not used), with a 10-minute in-process cache keyed by a
   token hash and a 15-second timeout. A failure gives no models.
7. **Add-on.** A GitHub-only user has no default model and gets the existing no-provider gate, reworded to say that an
   ordinary provider is still needed.
8. **Failures a user can act on.**
   - A GitHub model whose provider is gone gets **"GitHub isn't connected"** (chat) or a planner load error (fleet),
     never `Resolve`'s fallback onto the BYOK default.
   - A token GitHub rejects before any output becomes a **"Sign in with GitHub again"** reply (`GitHubAuthFailureAgent`),
     instead of a dead stream.
   - The in-process twin (DevHost, tests) refuses these models up front.
9. **Egress (an ADR 0013 extension, rule 13).** Conversation content, tool results and the workspace files the shell
   reads go to GitHub, and through it to the model vendors behind Copilot. It is per-user and opt-in: the user signs in.
   - Measured hosts during a turn were GitHub only: `140.82.121.5` (api.github.com) and `140.82.112.21` (a GitHub
     `iad` load balancer).
   - **`EnableSessionTelemetry = false` is set, but its effect is UNVERIFIED.** The telemetry hosts resolve into the
     same GitHub address ranges as inference, so an IP-level capture cannot separate them.

## Alternatives considered
- **A pasted token (a fine-grained PAT with Copilot Requests).** It lost to the user's choice of sign-in, and it was
  never verified against the runtime. It remains the fallback if the OAuth App is ever unavailable.
- **A GitHub App** (expiring `ghu_` tokens plus refresh). It lost because it needs a refresh-and-persist path (the ADR
  0081 machinery) for no gain the spike showed. It is the plan B if Copilot refuses OAuth-App tokens.
- **Client-level `CopilotClientOptions.GitHubToken` / `UseLoggedInUser`.** It lost because it signs the whole runtime in
  as one account, which is wrong for a multi-user runtime.
- **An `IChatClient` over a Copilot session, so the models work in plain chats.** It lost because it fights the SDK's own
  agent loop, and the user chose to hide the models from plain chats.
- **Calling `api.githubcopilot.com` directly.** It lost because it is an undocumented token exchange, unsupported, and
  likely against the Copilot terms for a hosted third party.
- **The kind as an option in the provider dialog.** It lost to a dedicated "Sign in with GitHub" button: the dialog's
  endpoint, key and model fields would all have had to hide for it, and its Save would be refused by the write guard
  anyway.
- **Copilot models as the default or the planner for GitHub-only users.** It lost because it breaks ADR 0019's
  "providers ⟺ a usable default", and the fleet planner, judge and manager would have to run on the Copilot runtime.

## Consequences
- Copilot-kind chats and fleet Copilot agents can run on Claude, GPT and Gemini models from the user's Copilot plan,
  billed to that plan. BYOK Copilot agents are unchanged.
- **Accepted risks.**
  - The token is in the runtime process's memory and crosses its stdio. With built-in tools on (ADR 0107) the shell runs
    as the app's user, so the existing `/proc/<pid>/environ` residual extends to the runtime's memory: the token is not
    in any environment, but a same-user process could in principle read another's memory where ptrace is allowed.
  - Built-in tools stay on for GitHub-mode sessions.
- **Still open, needing the user's OAuth App** (plan Step 0):
  - 0.1, an Agent42-OAuth-App token, and the minimum `Scopes` (shipped empty);
  - 0.2, whether GitHub honours PKCE on the exchange (it is sent);
  - 0.10, an account without a Copilot plan (the picker then shows none).
- **Manual prerequisites:** the `GH_OAUTH_CLIENT_SECRET` secret, `GitHub:OAuth:ClientId` in `appsettings.json`, and
  the callback `https://chat.agent42.nl/api/providers/github/callback` registered on the OAuth App.
- **Out of scope:** revoking the grant at GitHub on disconnect, Copilot quota display, GitHub Enterprise hosts, Copilot's
  native GitHub MCP tools, and the usage dashboard (copilot turns already produce no `chat` spans, ADR 0085).
- No migration: a new enum member read by name, a new grain state, and no new fields on existing state.
