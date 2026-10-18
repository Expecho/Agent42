planned-against: main @ f091432 (executed on branch `feature/anthropic-provider`, worktree `.claude/worktrees/feature-anthropic-provider`)

# Plan: native Anthropic (Claude) model provider

## Overview

Add `ProviderKind.Anthropic`: a per-user provider (ADR 0018) that reaches Claude through the **official Anthropic
C# SDK's `IChatClient`** (`AsIChatClient`), not the OpenAI-compatibility endpoint (which drops prompt caching and
proper thinking). It must work everywhere a chat model works today: ordinary chats, stored agents / agent-run,
the research agent, and the **agent fleet** (planner, judge, moderator, Magentic manager, team agents). Why now:
the user wants Claude as a first-class model. The provider layer already isolates each provider behind a
`Func<string, IChatClient>`, so this is mostly additive. The real work is a thin compatibility shim, because
current Claude models reject request shapes the app (or Agent Framework internals) send today.

## Affected areas (verified against the tree at f091432)

- `src/Agent42.Backend/Models/ModelProviderOptions.cs`: the `ProviderKind` enum.
- `src/Agent42.Backend/Models/ModelProviderBuilder.cs`: `TryBuildProvider`. It currently sends **every**
  non-`AzureOpenAI` kind down the OpenAI-compatible branch (`:107` and the fall-through at `:151-168`), so a new enum
  member silently builds an OpenAI client unless it gets an explicit branch.
- `src/Agent42.Backend/Models/ChatClientFactory.cs`: the new `Provider.Anthropic(...)` factory. `GetCapabilities` already
  reads declared flags for every non-Foundry kind, so it needs no change.
- New file `src/Agent42.Backend/Models/AnthropicCompatibilityChatClient.cs`: the shim (a `DelegatingChatClient`).
- `src/Agent42.Backend/Models/AgentApi.Models.cs`: `ValidateProvider`'s kind error message (`:151-154`)
  hard-codes the two kinds.
- `src/Agent42.Backend/Models/UserProviderResolver.cs`: kind parse (`:88`). It already uses
  `Enum.TryParse`, so it should need no change. Verify with a test.
- `src/Agent42.Backend/wwwroot/index.html` (`:870-873` kind `<select>`, `:884-885` endpoint input) and
  `src/Agent42.Backend/wwwroot/js/providers.js` (`syncProviderKindUi` `:208-211`, default kind `:321`).
- `src/Agent42.Backend/wwwroot/sw.js:5`: the `CACHE` bump. The current value `agent42-shell-v62` is pinned by
  `tests/Agent42.Tests/SearchAssetsTests.cs:127`, `ResearchAssetsTests.cs:33`, `PwaAssetsTests.cs:306` and
  `ClientToolsAssetsTests.cs:100`. All four must move together.
- `Directory.Packages.props` + `src/Agent42.Backend/Agent42.Backend.csproj`: the `Anthropic` package.
- Tests: `tests/Agent42.Tests/ModelProviderTests.cs`, `ProvidersApiTests.cs`, `UserProviderResolverTests.cs`,
  `FleetCopilotAgentFactoryTests.cs`, `CopilotRuntimeTests.cs`, `LiveModelChatTests.cs` (pattern only), plus new
  `AnthropicProviderTests.cs` / `AnthropicCompatibilityChatClientTests.cs` / `ModeratedGroupChatManagerTests.cs` /
  `LiveAnthropicChatTests.cs`, and the `.gitignore` entry for the live test's local key file.
- Docs: new ADR (next free number, **0102 at plan time; re-check, since two numbering collisions already
  happened, see #197**), `.agentic/gotchas.md` new entries, and the CLAUDE.md feature-map row for "Model providers".

## Context & constraints (what the executor cannot cheaply re-derive)

### SDK facts (read from source at the `Anthropic` 12.58.0 release commit, 2026-10-18; re-verify on the version `dotnet add` resolves)
- `AsIChatClient(this IAnthropicClient, string? defaultModelId, int? defaultMaxOutputTokens, AnthropicThinkingMode
  thinkingMode = Adaptive)` lives in namespace `Microsoft.Extensions.AI` (`AnthropicClientExtensions`). It depends on
  M.E.AI.Abstractions ≥ 10.5.1, so it is compatible with our 10.10.x.
- **`max_tokens` defaults to a hard-coded 1024** when neither `ChatOptions.MaxOutputTokens` nor
  `defaultMaxOutputTokens` is set. A chat reply, and any long `create_artifact` tool argument, would be silently
  truncated. Always pass `defaultMaxOutputTokens`.
- `AnthropicClient` init props: `ApiKey`, `AuthToken`, `BaseUrl`, `MaxRetries`, `Timeout`, `HttpClient`. **`ApiKey`,
  `AuthToken` and `BaseUrl` fall back to the `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_BASE_URL`
  env vars.** Set all of them explicitly from the user's stored provider, so nothing ambient leaks in (the
  per-user, no-operator-model rule, ADR 0019/0030). Do **not** inject an `IHttpClientFactory` client (cross-cutting
  rule 8). Keep the SDK's own default retries (2), for parity with the OpenAI SDK default the other providers use.
- `ToolMode.RequireAny` maps to `tool_choice:any` and `RequireSpecific` to `{type:tool}`. **Current Claude models (Opus 5.5,
  Sonnet 5.5, Fable 5.1) reject both with a 400.** `Temperature`/`TopP`/`TopK` are forwarded when set, and current
  models reject non-default values with a 400. `Reasoning.Effort = None` maps to `thinking:{type:disabled}`, which is a
  400 on Opus 5.5 and Sonnet 5.5. `Reasoning.Output` maps `None` to `display:omitted` and `Summary`/`Full` to
  `display:summarized`. That mapping is harmless and is what ADR 0071's flag already sends.
- Thinking round-trips: the SDK maps a `ThinkingBlock` to `TextReasoningContent { ProtectedData = signature }`, and back to a
  `thinking` block with the signature. While streaming, the signature arrives as a **separate signature-only
  `TextReasoningContent`** update. Whether M.E.AI's coalescing (which `FunctionInvokingChatClient` uses to
  rebuild the assistant message for the next tool-loop iteration) keeps the signature attached is **UNVERIFIED**.
  Step 1 pins it.
- `RawRepresentationFactory` returning `MessageCreateParams` makes the adapter take `Model` and `MaxTokens` **from
  the params and ignore `options.ModelId`/`MaxOutputTokens` and the client defaults**. Anything that uses it must
  copy both in. That is also the only way to set top-level automatic `CacheControl`.
- `UriContent` image maps to a URL image source (supported). `DataContent` maps to base64. Unsupported media types are
  **dropped silently**. Whitespace-only text blocks are dropped, so a message can end up with **zero** blocks, which
  the API rejects.
- Usage: `CachedInputTokenCount` = cache reads. (The usage dashboard does not read cache tokens from spans; see the
  gotcha "The usage dashboard counts tokens from `chat` spans ONLY". Nothing to do here.)
- The SDK emits its own OTel spans on source `com.anthropic.sdk.csharp`. We do **not** `AddSource` it. The M.E.AI
  `UseOpenTelemetry` wrapper in `ChatClientFactory.Build` already covers the `chat` spans the dashboard reads.

### Agent42 facts that shape the design
- **Thinking is never stored** (ADR 0071): `StripReasoningFromStorage` is the history provider's response filter
  (`src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:148-155`, `AgentGrain.cs:1164-1221`), and it filters the
  in-memory history too. So **no thinking block is ever replayed across turns**, and Claude's "preserved thinking"
  history-editing check (signatures bound to the conversation prefix; edited history gives a 400 on accounts
  created ≥ 2026-08-31) has nothing to check across turns. Edit/regenerate (0092), fork (0092),
  `CompactToolPayloads` (0084/0098), retention pruning (0016/0068), the research fold (0082), model switching and
  `StripToolHistory` all operate on reasoning-free history and need **no change**. Removing every thinking block
  from **previous turns** is the documented-valid "strip" shape. This is scoped to previous turns only: within the current tool
  loop, the thinking block before a pending `tool_use` must go back (see below). **Do not "fix" ADR 0071 to
  store thinking for Claude.** That would
  re-introduce the edit-history 400s and the 2 MB pressure (ADR 0004).
- What remains exposed is **within one turn**: in a tool loop, `FunctionInvokingChatClient` re-sends the assistant
  message (thinking + `tool_use`) in the next request. That block must carry its signature intact (Step 1).
  Rotating SAS URLs in earlier user turns are fine for the binding check, because the bytes are bound, not the URL.
- Hard-400 sites found in src (scout, 2026-10-18):
  - `src/Agent42.Backend/Fleet/ModeratedGroupChatManager.cs:85` uses `ToolMode = ChatToolMode.RequireAny`. The method
    already degrades to round-robin when there is no tool call, so `Auto` is safe.
  - `src/Agent42.Agents/Conversations/AgentGrain.FollowUps.cs:46-49` sets `Reasoning.Output = None` only (no `Effort`),
    which maps to `display:omitted` and is **not** a 400. It is listed so nobody "fixes" it.
  - Agent Framework internals we cannot edit may also force tool choice, send sampling params, or end a request on an
    assistant message: the Magentic manager, `AIJudgeLoopEvaluator` (research judge), and the declarative fleet agent YAML,
    whose sampling params the planner model could write. Sequential/handoff workflows may hand an agent a history
    ending on an assistant turn, and current Claude models reject an assistant prefill with a 400. **Unverified which
    of these actually occur**, which is why the fix lives at the provider boundary, not at each call site.
- Fleet picks models from the catalog's declared flags (`FleetTaskService.cs:64`, `IsToolCapable`), so an Anthropic model
  declared with `SupportsTools` is offered. A fleet **Copilot** agent on a provider with no Copilot settings already
  degrades cleanly: `FleetCopilotAgentFactory.cs:111-112` throws a rejection that `LoadAgentsAsync` turns into planner
  feedback, and the chat Copilot kind refuses turns via `CopilotUnavailableException(ProviderUnsupported)`
  (`src/Agent42.Backend/Copilot/CopilotAgentFactory.cs:82-85`). So `copilot: null` needs no new code, only tests.
- Fleet document search uses the per-user **embedding** model (`FleetDocumentTools.cs:75`). Anthropic has no
  embeddings, so a Claude-only user has no document search. That matches the existing "embedding model is a separate
  per-user pick" rule (ADR 0030) and needs no new code. Say so in the ADR.
- The user endpoint is deliberately **not SSRF-guarded** (gotcha "A user provider overlay is best-effort…"). Keep the
  same single rule for Anthropic: absolute http/https, required. The UI pre-fills `https://api.anthropic.com` when
  the kind is switched to Anthropic and the endpoint is empty. **Do not** make the endpoint optional per kind: one
  validation rule, no new branch.

### Decisions taken in this plan (rationale goes into the ADR)
1. **Native SDK over the OpenAI-compat endpoint.** The compat endpoint is evaluation-grade: it has no caching and no proper
   thinking. It still works today with no code, via an OpenAICompatible provider. The ADR records that as the rejected alternative.
2. **One compatibility shim at the provider boundary** (`AnthropicCompatibilityChatClient : DelegatingChatClient`),
   applied only inside `Provider.Anthropic`. It is not spread over call sites, because AF internals cannot be patched. Per request it:
   - maps `ToolMode` `RequireAny`/`RequireSpecific` to `Auto`;
   - nulls `Temperature`, `TopP` and `TopK`;
   - maps `Reasoning.Effort == None` to `Low` (thinking cannot be disabled on current models; low effort is the
     documented substitute), and leaves `Output` alone;
   - when the last message has role `Assistant` **and carries no `FunctionCallContent`**, appends a `User` message
     `"Continue."`, because an assistant prefill is rejected. An assistant message ending in a tool call is a pending tool
     round. It is left untouched, since appending there would break the `tool_use`/`tool_result` pairing;
   - drops any message that would reach the wire with zero sendable blocks (whitespace-only text, unsupported media).
     It **never** drops a message carrying `FunctionCallContent` or `FunctionResultContent`, because an orphaned
     `tool_result` is a 400;
   - sets top-level automatic prompt caching via `RawRepresentationFactory` returning `MessageCreateParams { Model =
     <bare model id>, MaxTokens = options.MaxOutputTokens ?? default, CacheControl = ephemeral }`. If the caller
     already set a factory, the shim calls it first. When the result is a `MessageCreateParams`, the shim fills only
     the absent `CacheControl`/`Model`/`MaxTokens`. Any other result is passed through unchanged, with no caching, so the caller wins.
   - **It must never touch `Reasoning`.** `ChatClientFactory.Build` wraps the shim (`provider.Client(model).AsBuilder()`), so
     `ConfigureOptions(Reasoning ??= Summary)` and `UseOpenTelemetry` run **outside** it. The shim receives the final
     options, and AC8 depends on it passing `Reasoning.Output` through. The only exception is the `Effort == None` rewrite above.

   It **always clones** `ChatOptions` and the message list and never mutates the caller's objects. The messages are the
   grain's live session history, and a mutation would be persisted (rule 5 / ADR 0004). Applying it to every
   Claude model is deliberate. Older models accept the downgraded shapes too, and a per-model matrix is
   drift.
3. **Default max output tokens = 32 000**, as a named constant next to the shim. Interactive chat streams.
   Non-streaming callers are the fleet moderator/planner/judge (`GetResponseAsync`), the research judge, and the reminder
   `RunAsync` path; follow-ups pass their own 400. **It is unverified that the .NET SDK accepts a non-streaming 32 000
   request** (some Anthropic SDKs refuse non-streaming requests whose `max_tokens` implies a long run). Step 1 pins it. If
   it refuses, the shim caps `MaxOutputTokens` at the largest value the SDK accepts **on the non-streaming path only**,
   and the ADR records the cap. This is not tunable per provider in this MR.
3b. **Retries: `MaxRetries` is set explicitly, never left to the SDK default.** The repo's concern is
   that retries multiply the stall on a non-reentrant `AgentGrain` turn (gotcha "A named HttpClient silently inherits
   AddServiceDefaults' retry pipeline"). That concern is about a retry layer **stacked on top of** the SDK's own, and the
   OpenAI SDK clients the other providers use keep their own default retry policy. So the decision is: set `MaxRetries`
   to the same count as the OpenAI SDK's default `ClientRetryPolicy` (executor: read it from the pinned `System.ClientModel`, don't
   recall it). That gives parity with the existing providers, it is stated in code, and it keeps the SDK's `retry-after`
   handling for Anthropic 429s (`MaxRetries = 0` would discard it). The ADR records this reasoning.
4. **Thinking mode Adaptive** (the SDK default). ADR 0071's `SupportsReasoning` checkbox keeps its meaning. Ticked
   means `Reasoning.Output = Summary`, mapped to `display:summarized`, so the user sees thinking. Unticked means
   `display` is unset (omitted on current models): the model still thinks, but nothing is shown. The ADR and the UI
   hint must say "tick Reasoning to see Claude's thinking".
5. **Capabilities are declared-only** (like OpenAICompatible): no discovery, `SupportsHostedTools` false. Claude's
   server-side web search/fetch is out of scope. The SDK maps `HostedWebSearchTool` to an outdated tool version anyway.
   There are no image, embedding or speech funcs, and `copilot: null`.
6. **Egress (rule 13):** this is an ADR 0013 extension. Conversation content, tool results and **image SAS URLs** for
   models declared `SupportsVision` go to Anthropic. It is per-user and opt-in (a user adds the provider with their own
   key), the same shape as ADR 0018. It is not an always-on provider.

## Steps

Each step names its narrowest verifying command. Steps 3 and 4 are parallelizable after Step 2. Everything else is
sequential.

### Step 1: package, SDK seam, and the signature round-trip pin (this gates the design)
- Run `dotnet add src/Agent42.Backend/Agent42.Backend.csproj package Anthropic` and let it resolve the latest stable
  version. CPM means the version lands in `Directory.Packages.props` (hard rule 4; never type a version). Also add the
  package to the test project if the tests construct SDK types directly.
- Write `tests/Agent42.Tests/AnthropicProviderTests.cs` with a stub `HttpMessageHandler` (copy the stub-transport
  shape of `tests/Agent42.Tests/ReasoningStreamTests.cs`: no network). The handler records request bodies and replays canned
  Messages-API **SSE**. Pin the following, where the IChatClient is built the way Step 2 will build it (`AsIChatClient` over an
  `AnthropicClient { ApiKey, AuthToken = null, BaseUrl, HttpClient = new HttpClient(stub) }`, wrapped in
  `FunctionInvokingChatClient`):
  - **(a) The signature survives a streamed tool loop.** Response 1 streams `thinking` + `signature_delta` +
    `tool_use`, the FICC invokes a dummy `AIFunction`, and request 2's body must contain the assistant
    `thinking` block **with the same signature**, immediately before the `tool_use`.
  - **(b)** The same check on the non-streaming path.
  - **(c)** A non-streaming request with `max_tokens` 32 000 is sent, not refused client-side by the SDK (decision 3).
- **If (a) or (b) fails** (coalescing loses or splits the signature): **STOP and return to the user.** Stripping
  thinking from the *current* tool loop is **not** a known-valid shape. With thinking on, the assistant turn before a
  pending `tool_result` is expected to keep its thinking block, so a strip is likely a 400 that only the live test
  (Step 6) would reveal. The candidate recoveries need the user's call:
  - a shim step that re-merges a signature-only `TextReasoningContent` into the preceding reasoning part before re-send;
  - pinning a different thinking mode;
  - accepting the strip after a live check.

  Record the branch taken in the ADR.
- Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~AnthropicProviderTests`.

### Step 2: `ProviderKind.Anthropic`, the builder branch, `Provider.Anthropic`
- Add an `Anthropic` member to `ProviderKind` **at the end** (the enum's default 0 stays `AzureOpenAI`, and
  config/stored strings bind by name). Give it a doc comment in the existing style.
- `ModelProviderBuilder.TryBuildProvider`: add an explicit `ProviderKind.Anthropic` branch with the same skip/invalid rules as
  OpenAICompatible (blank key: Skipped; blank endpoint: Invalid). It returns declared models. Keep the method under
  S3776 = 15 (rule 9). If needed, extract the OpenAI-compatible tail into a helper rather than nesting.
- `MultiProviderChatClientFactory.Provider.Anthropic(ProviderOptions options, Uri endpoint, string apiKey, HttpMessageHandler?
  handler = null)`: build one `AnthropicClient` (explicit `ApiKey`, `AuthToken = null`, `BaseUrl = endpoint`). Return
  `new Provider(options, model => new AnthropicCompatibilityChatClient(client.AsIChatClient(model,
  AnthropicLimits.DefaultMaxOutputTokens), model))` with no image, embedding, speech or copilot funcs. The handler parameter
  is only the test seam. Doc comment: why not OpenAICompatible, why the shim, and that the endpoint comes from config only
  (the SSRF invariant comment at the class top applies).
- `ValidateProvider`: change the kind error message to list all three kinds (derive it from `Enum.GetNames<ProviderKind>()`,
  so it can't drift again).
- Tests (`ModelProviderTests.cs` / `ProvidersApiTests.cs` / `UserProviderResolverTests.cs`, copying the neighbouring test shapes):
  - the builder builds Anthropic and **not** OpenAI (assert via `GetService<ChatClientMetadata>()`: provider name
    `"anthropic"`);
  - a blank key is Skipped and a blank endpoint is Invalid;
  - the API accepts `kind = "Anthropic"` (case-insensitive) and masks the key;
  - the resolver parses a stored `"Anthropic"` overlay into a built provider;
  - `GetCapabilities` returns exactly the declared flags, with `SupportsHostedTools` false;
  - `GetImageGenerationService`, `GetEmbeddingGenerator` and `GetSpeechService` all return the no-op/null for an Anthropic model;
  - `GetCopilotProvider` returns null.
- The stub-transport request-body tests (shared helper with Step 1):
  - the `x-api-key` header is the stored key and `ANTHROPIC_API_KEY` set in the environment is **not** used. Set it in-test
    and restore it after. The env-touching tests go in a new `[Collection("AnthropicEnvironment")]` with
    `DisableParallelization = true`, following the existing `[Collection("LiveTelemetry")]` shape, because process env is global;
  - `max_tokens` = 32000 when unset;
  - a declared reasoning model sends `display:"summarized"` (via `ChatClientFactory.Build`'s existing
    `RequestsReasoning` wiring, end-to-end through `GetClient`).
- Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ModelProviderTests|FullyQualifiedName~ProvidersApiTests|FullyQualifiedName~UserProviderResolverTests|FullyQualifiedName~AnthropicProviderTests"`.

### Step 3: `AnthropicCompatibilityChatClient` (parallelizable with Step 4)
- Implement decision 2. Keep each rewrite a small private static method (S3776). Cover both `GetResponseAsync` and
  `GetStreamingResponseAsync`.
- `tests/Agent42.Tests/AnthropicCompatibilityChatClientTests.cs`: use a recording inner `IChatClient` to unit-test each
  rewrite. Also test that **the caller's `ChatOptions` and message list are unmodified** after the call (reference
  and content equality), and that a caller-supplied `RawRepresentationFactory`'s params are kept, with only
  `CacheControl`/`Model`/`MaxTokens` filled in when absent.
- Negative cases, each its own test:
  - a trailing assistant message carrying `FunctionCallContent` gets **no** "Continue.";
  - a message with tool content is never dropped, even if its text is blank;
  - a caller factory returning a non-`MessageCreateParams` object passes through untouched.
- Stub-transport tests through the real SDK, all with the shim's factory **active** (the RawRepresentationFactory trap
  is the single riskiest SDK assumption, so pin every field it could swallow):
  - `ChatToolMode.RequireAny` in, `tool_choice` `auto` on the wire (or absent);
  - `temperature` absent;
  - top-level `cache_control` present;
  - `model` and `max_tokens` correct;
  - **`tools` still present**, `system` still carries `Instructions`;
  - with `Reasoning.Output = Summary`, **`thinking.display = "summarized"` still present**.

  If any of these is lost under the factory, drop the factory approach. Caching then goes per-block instead: the
  shim clones the last message and applies the SDK's `WithCacheControl` to its last content. The ADR records which was used.
- Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~AnthropicCompatibility`.

### Step 4: Fleet and agent-kind coverage (parallelizable with Step 3)
- Run the fleet against an Anthropic-kind model through the existing fleet test doubles (`FakeChatClient.Fleet.cs`,
  `FleetTestDoubles.cs`, `FleetTeamRunnerTests.cs`). The point is not to retest the fleet. It is to prove the
  **moderator path** sends no forced tool choice once wrapped. Write a test that runs `ModeratedGroupChatManager` over
  the shim with a recording inner client: `ToolMode` arrives as `Auto`, and a tool-call reply still selects the named
  speaker. Add a second test where the reply is **prose with no tool call**: the chat continues round-robin and does not
  finish. That is a silent quality degradation under `Auto`, and the ADR states it. Consider one moderator-prompt
  sentence ("Always answer by calling `next_speaker` or `finish`.") in the `moderator` prompt template, since it is harmless for
  OpenAI too. Make that change only if the prompt template lives in this repo, and pin it with the existing prompt tests.
- `FleetCopilotAgentFactoryTests.cs`: add a case where the per-user factory's model is an Anthropic provider, so
  `GetCopilotProvider` returns null and the existing rejection message reaches the planner as an agent-load error, not
  an exception escaping the run.
- Chat Copilot kind: in `tests/Agent42.Tests/CopilotRuntimeTests.cs`, next to
  `CopilotProviderMapping_FoundryAndOpenAICompatible`, add a test that an Anthropic provider maps to **no** Copilot
  settings, and that `CopilotAgentFactory` then throws `CopilotUnavailableException` with reason `ProviderUnsupported`.
- Do **not** change `ModeratedGroupChatManager` itself. The shim owns the downgrade, and OpenAI providers keep
  `RequireAny`.
- Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~Fleet|FullyQualifiedName~Copilot"`.

### Step 5: Frontend
- `index.html`: add `<option value="Anthropic">Anthropic (Claude)</option>` to the kind select.
- `providers.js` `syncProviderKindUi`: keep the Responses field Foundry-only. When the kind becomes `Anthropic` and the endpoint
  is empty, fill it with `https://api.anthropic.com`. Never overwrite a non-empty endpoint. Add a one-line hint that
  Claude models should be declared with Tools/Vision as appropriate, and with Reasoning ticked to show thinking.
- Bump `CACHE` in `sw.js` (v62 → v63, or whatever is next on the branch at execution time) and update the four tests
  that pin it (listed under Affected areas). Rule 3.
- Add a source-text guard test (in the existing assets-test style, e.g. next to the other `*AssetsTests`) asserting the
  `Anthropic` option exists in `index.html` and that `providers.js` pre-fills the endpoint only when it is empty.
- Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~AssetsTests"`, then a manual check in
  DevHost (`dotnet run --project tools/Agent42.DevHost`): add an Anthropic provider and confirm it saves, the key is
  masked, and the model appears in the picker. DevHost's canned LLM means no real Claude call happens there.

### Step 6: Live smoke (env-gated, run manually before the MR)
- `tests/Agent42.Tests/LiveAnthropicChatTests.cs`: copy the `LiveModelFactAttribute`/`LiveModelSettings` shape
  (`tests/Agent42.Tests/LiveModelChatTests.cs:18-60`) with its own git-ignored local file and env var (e.g.
  `AGENT42_LIVE_ANTHROPIC_KEY`, **not** `ANTHROPIC_API_KEY`, which the SDK would pick up ambiently). Add the local file to
  `.gitignore`. Model id from the file, defaulting to `claude-sonnet-5-5`. Live tests:
  1. a streamed multi-tool-call turn through `ChatClientAgent` + FICC with `Reasoning.Output = Summary`: it completes,
     reasoning text surfaces, and no 400 occurs. This is the real-wire twin of Step 1(a);
  2. a two-turn conversation via the real grain path, where turn 2 sends no thinking block (ADR 0071 strip) and succeeds;
  3. a forced-tool-mode request and a trailing-assistant request through the shim both succeed;
  4. an image turn via a public test-image `UriContent`. **The private-blob SAS URL path stays unverified live**, because the
     test cannot mint one. The ADR lists this as a post-deploy prod check;
  5. a non-streaming 32 000-`max_tokens` request completes (the real-wire twin of Step 1(c)).
- The user must supply the key. **Ask the user to run these.** The executor reports SKIPPED if no key is available,
  never PASS.
- Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~LiveAnthropic` (with the key present).

### Step 7: Docs (same MR, rule 2)
- `/decide`: write a new ADR "Anthropic provider kind" (`extends: [0013, 0018]`, mention 0071, 0019, 0097 and 0085). Record
  decisions 1–6, the Step 1 branch taken, the rejected alternatives (OpenAI-compat endpoint; per-call-site fixes; storing
  thinking for Claude; making the endpoint optional; Foundry-hosted Claude as a follow-up via `Anthropic.Foundry`), and the
  egress statement (rule 13).
- `/gotcha`, three entries, each with evidence paths:
  1. The Anthropic SDK reads `ANTHROPIC_*` env vars and defaults `max_tokens` to 1024. Set both explicitly.
  2. Current Claude models 400 on forced tool choice, sampling params, disabled thinking and assistant prefill. The shim
     is the single home for these, so never patch call sites.
  3. A `RawRepresentationFactory` returning `MessageCreateParams` silently overrides `ModelId`/`MaxOutputTokens`.
- Also add a line to the existing reasoning-strip gotcha. That strip is also what keeps Claude off the preserved-thinking
  edit-history 400, so **never** store reasoning for an Anthropic provider.
- CLAUDE.md feature map: add the new ADR number to the "Model providers" row.
- Doc comments that enumerate the kinds become stale in this MR (rule 2), so fix them:
  - `ModelProviderOptions.cs` (`ConfiguredModel` doc `:19-20`; `Endpoint`/`ApiKey`/`UseResponsesApi` docs `:100-141`);
  - `ChatClientFactory.cs`: the `Provider.Copilot` doc ("Set by Foundry and OpenAICompatible, the only places"), and the
    `SpeechService`/`EmbeddingGenerator` docs ("Set on every provider (both kinds…)");
  - the `GetCapabilities` comment;
  - grep ADR 0018 and 0071 for kind enumerations and add an "Amended by" note where they list "both kinds".
- Run `/sync-docs` over the diff.

### Step 8: Gates and MR
- Run `dotnet build Agent42.slnx` (S3776 errors fail here), `dotnet format Agent42.slnx --verify-no-changes`, `dotnet test
  Agent42.slnx --collect:"XPlat Code Coverage"`, then `node .agentic/scripts/check-new-code-coverage.mjs`.
- Run the `critic` on the diff. Open an MR from `feature/anthropic-provider`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A tool loop (streamed and non-streamed) re-sends the thinking block with its signature intact; a non-streaming 32 000-token request is not refused | `StreamedToolLoop_ResendsThinkingSignature`, `NonStreamedToolLoop_ResendsThinkingSignature`, `NonStreaming_MaxTokens32k_IsSent` | `AnthropicProviderTests.cs` |
| AC2 | `Anthropic` kind builds an Anthropic client, not an OpenAI one | `AnthropicKind_BuildsAnthropicClient` | `ModelProviderTests.cs` |
| AC3 | Blank key skipped, blank endpoint invalid | `AnthropicBlankKey_IsSkipped`, `AnthropicBlankEndpoint_IsInvalid` | `ModelProviderTests.cs` |
| AC4 | API accepts and masks an Anthropic provider; the error message lists all kinds | `Providers_AcceptAnthropic_MaskKey`, `UnknownKind_MessageListsAllKinds` | `ProvidersApiTests.cs` |
| AC5 | Stored overlay `"Anthropic"` resolves | `AddsAnthropicProvider` | `UserProviderResolverTests.cs` |
| AC6 | Capabilities are declared-only; no image, embedding, speech or copilot | `AnthropicModel_CapabilitiesAreDeclared_NoSideServices` | `ModelProviderTests.cs` |
| AC7 | Key comes only from config, not the env; `max_tokens` = 32000 by default | `AnthropicRequest_UsesStoredKey_NotEnvironment`, `AnthropicRequest_DefaultsMaxTokens` | `AnthropicProviderTests.cs` |
| AC8 | A declared reasoning model sends `display:summarized` end-to-end | `AnthropicReasoningModel_RequestsSummarizedThinking` | `AnthropicProviderTests.cs` |
| AC9 | Forced tool mode, sampling params, `Effort = None` and a trailing assistant message are rewritten; empty messages are dropped; a pending tool-call message gets no "Continue."; a tool-content message is never dropped; `Reasoning.Output` passes through | one test per rewrite plus the negative cases | `AnthropicCompatibilityChatClientTests.cs` |
| AC10 | The shim never mutates the caller's options or messages | `Shim_DoesNotMutateCallerState` | `AnthropicCompatibilityChatClientTests.cs` |
| AC11 | Under the shim's factory, the wire has top-level `cache_control` and the correct `model`, `max_tokens`, `tools`, `system` and `thinking.display`; a foreign caller factory passes through | `Wire_UnderFactory_KeepsEveryField`, `ForeignFactory_PassesThrough` | `AnthropicCompatibilityChatClientTests.cs` |
| AC12 | The fleet moderator over Claude sends no forced tool choice; a tool reply selects the speaker; a prose reply falls back to round-robin without finishing | `Moderator_OverAnthropicShim_UsesAutoAndSelectsSpeaker`, `Moderator_ProseReply_FallsBackToRoundRobin` | new `ModeratedGroupChatManagerTests.cs` |
| AC13 | A fleet Copilot agent on Claude is rejected as planner feedback, not a crash | `CopilotAgent_OnAnthropicModel_IsRejectedToPlanner` | `FleetCopilotAgentFactoryTests.cs` |
| AC14 | The chat Copilot kind on Claude reports `ProviderUnsupported` | `CopilotKind_OnAnthropicModel_IsProviderUnsupported` | `CopilotRuntimeTests.cs` |
| AC15 | The UI offers Anthropic and pre-fills the endpoint only when empty; `CACHE` is bumped | `ProvidersUi_OffersAnthropic_PrefillsEmptyEndpoint` + the 4 updated CACHE pins | `*AssetsTests.cs` |
| AC16 | Real Claude: streamed tool loop with thinking, two-turn chat, shim rewrites, image turn, non-streaming 32k | `LiveAnthropic_*` (env-gated, manual; SKIPPED without a key, never PASS) | `LiveAnthropicChatTests.cs` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at build time (an error fails `dotnet build`) |
| `vulnerable_packages` | 0 | CI package-freshness job |
| `outdated_packages` | report | CI (soft) |
| `add_policy` | latest-stable | `dotnet add package` resolves; never typed |
| CI hard blocks | build, tests, `dotnet format`, vulnerable packages, doc-path integrity, new-code coverage | `.github/workflows/pr-checks.yml` |

## Out of scope

- Claude on Microsoft Foundry (`Anthropic.Foundry`, Messages API at `/anthropic/v1`). Planned as a follow-up kind. The ADR
  names it.
- Claude server tools (web search/fetch, code execution), Files API, server-side compaction, context editing,
  fast mode, refusal fallbacks (`fallbacks: "default"`), and per-message effort. `stop_reason: refusal` surfaces as
  M.E.AI `ContentFilter` and is handled like any other provider's finish.
- A Copilot runtime mapping for Anthropic (`CopilotProviderSettings`). It needs its own verification of the Copilot BYOK provider types.
- Storing or replaying thinking across turns (ADR 0071 stays as is, deliberately).
- Per-provider tuning of `max_tokens`/effort in the UI.
- Cache-token reporting on the usage dashboard.
- Optimising prompt-cache prefix stability (per-run `AIContext` instructions from shared state and memory vary per turn).
  Caching is enabled. Making it hit more often is a later cost lever, measured with `usage.cache_read_input_tokens`.

## Review (critic, 2026-10-18): no blockers, 3 major and 5 minor; all accepted

| # | Finding | Handling |
|---|---|---|
| M1 | The Step 1 fallback (strip all thinking) is not known-valid inside the current tool loop | **Accepted.** If the signature pin fails, the fallback is now a STOP-and-return-to-user with candidate recoveries. The "documented-valid strip" wording is scoped to previous turns. |
| M2 | `RawRepresentationFactory` may swallow more than `Model`/`MaxTokens` | **Accepted.** The Step 3 wire test now pins `tools`, `system`, `tool_choice` and `thinking.display` under the factory. A foreign caller factory passes through. A per-block `WithCacheControl` fallback is named. |
| M3 | Non-streaming 32k `max_tokens` is unverified; retry "parity" is asserted, not justified | **Accepted.** Step 1(c) and live test 5 pin the non-streaming request, with a non-streaming-only cap as the named fallback. Decision 3b sets `MaxRetries` explicitly to the OpenAI SDK's default count, read from source, with the reasoning recorded. |
| m4 | The moderator under `Auto` can reply in prose, so selection silently degrades to round-robin | **Accepted.** There is a prose-path test, an ADR note, and an optional one-sentence moderator-prompt nudge. |
| m5 | "Continue." must not be appended after a pending tool call | **Accepted.** The rule is narrowed and has a negative test. |
| m6 | Dropping empty messages could orphan a `tool_result` | **Accepted.** Messages with tool content are never dropped, and this has a negative test. |
| m7 | The SAS-URL image path is never exercised live | **Accepted.** It is recorded as unverified, plus a post-deploy prod check. |
| m8 | Stale kind-enumerating doc comments; env-test collection is vague; AC14 file not named | **Accepted.** Step 7 lists the doc comments. A named `AnthropicEnvironment` collection is used. AC14 is in `CopilotRuntimeTests.cs`. |

## Implementation notes: where execution diverged from the plan (2026-10-18)

- **Step 1 gate passed on the first run.** The signature survives both the streamed tool loop (coalescing re-merges the
  separate `signature_delta` update) and the non-streamed one, and the SDK sends a non-streaming 32 000 request. No
  recovery branch and no non-streaming cap were needed.
- **Retries:** measured by reflection. The OpenAI `ClientRetryPolicy.Default` (System.ClientModel 1.15.0) uses 3, and
  the Anthropic SDK default is 2. `AnthropicLimits.MaxRetries = 3`.
- **The empty-message rule mirrors the SDK.** A wire probe showed the SDK itself skips a message left with zero
  blocks. It drops whitespace-only text, an empty unsigned thinking block, audio, `application/json` and a `text/*`
  `UriContent`. It sends images, PDFs, `text/*` `DataContent` (as a text document) and `HostedFileContent`. The shim
  drops the same set, so its trailing-assistant check sees the message that really ends the request
  (`DroppedTrailingUser_ExposesAssistant_WhichGetsContinue`). `Wire_EmptiedMessageIsSkipped_AndTrailingAssistantContinues`
  pins the SDK half.
- **Caller `MessageCreateParams`:** `Model`, `MaxTokens` and `Messages` are `required` init properties, so a caller's
  params cannot lack them. The shim fills only a missing `CacheControl` (via `with`, the params being a record).
- **The factory approach held.** `Wire_UnderFactory_KeepsEveryField` passed, so the per-block `WithCacheControl` fallback
  is not used.
- **`AnthropicClient` is not a record.** `Provider.Anthropic` therefore builds it in two branches (with and without the
  test handler) instead of using `with`.
- **The builder** gained `TryBuildDeclaredProvider`, shared by OpenAICompatible and Anthropic. The Invalid message is now
  `"... is an {Kind} provider ..."`, which is unchanged text for OpenAICompatible.
- **Shared test helpers:** `RecordingHttpHandler.Call` gained an optional `Headers` map (for `x-api-key`). There are
  new `AnthropicStub` (canned SSE/JSON) and `RecordingChatClient` files. Step 2's env test lives in its own
  `AnthropicEnvironmentTests.cs`.
- **Moderator:** the protected hooks of the sealed manager are driven by reflection. The prompt nudge was added
  (`ModeratorPrompt_AsksForAToolCall`).
- **Frontend:** a hidden `#providerAnthropicNote` hint, toggled with the kind. The guard test is in a new
  `ProvidersAssetsTests.cs`. `CACHE` is now v63.
- **The DevHost manual check is partial.** Save and key masking were confirmed over `/api/providers`. DevHost serves a
  fixed model catalog and never runs `UserProviderResolver`, so "the model appears in the picker" cannot be checked
  there. `UserProviderResolverTests.AddsAnthropicProvider` covers the merge.
- **Live tests (Step 6)** run at the factory-client level (`Provider.Anthropic` + shim + `Build`), not through the
  grain. Test 2 replays turn 1 as text only, which is the ADR 0071 strip shape. They are **SKIPPED**: no key was
  available in this session.
- **ADR 0018/0071** do not enumerate kinds. Following the critic, 0013 got an "Amended by" note (a new egress
  destination, same pattern as 0094/0097) and 0018 got an "Extended by" note. 0071 needs none.
  `docs/features/bring-your-own-models.md` and the remaining two-kind doc comments (`ModelProviderContracts`,
  `ModelCatalog`, `ModelProviderBuilder`, `ChatClientFactory`) were updated as well.
- **Flake seen:** `ChatStreamDisconnectTests.ClientDisconnectsDuringAClientToolWait_ResultStillResumesAndPersists` failed
  once in the full coverage run, then passed 3/3 alone. It is unrelated to this diff.
