# Plan — Call Agent 42 on a real phone number

planned-against: main @ 9ba20b7

## Overview

**Goal:** dial a real phone number from any phone (no app, no browser, no login) and hold a
spoken conversation with a *stored agent* — the actual `AgentGrain`, with its persona, tools,
MCP servers, RAG documents and history — over PSTN.

**Why now:** ADR 0037 shipped hands-free voice but it is browser-only and the user reports it
"works, but feels clunky". ADR 0037's own *Alternatives considered* recorded server-side realtime
as "the path to revisit if voice becomes central". Telephony forces part of that decision, but
this plan deliberately takes the **turn-based** half of it (see Context, decision D1).

**Shape:** Azure Communication Services (ACS) Call Automation answers the inbound call and uses
Azure AI Speech for both speech legs. Every agent reply is spoken as the **`Prompt` of the next
`Recognize` action** — one action per turn, not a Play followed by a Recognize. No WebSocket, no
realtime model, no second LLM. **Speech barge-in is not available** (F10); a DTMF keypress is the
only way to cut a reply short.

```
caller ──PSTN──► ACS ──Event Grid IncomingCall──► POST /api/telephony/incoming
                  │                                      │ resolve caller → (userId, agentId)
                  │◄──────────── AnswerCall ──────────────┘ BindAgentAsync + AnswerAsync
                  │
                  ├── Recognize(prompt: greeting)
                  │        └── RecognizeCompleted ──► POST /api/telephony/callback
                  │                                          │ 200 at once; detached turn (F5)
                  │                                          ▼
                  │                                    AgentGrain.ChatAsync
                  │                                          │ full reply text
                  │◄──────── Recognize(prompt: reply) ───────┘
                  │        speaks it, THEN listens ──► next turn
                  └── …loop until CallDisconnected
```

## Affected areas

Verified against the tree at `9ba20b7`.

**New — Agents (`src/Agent42.Agents/Telephony/`, flat `Agent42.Agents` namespace — ADR 0039):**
- `IPhoneNumberDirectoryGrain.cs`, `PhoneNumberDirectoryGrain.cs`
- `ICallSessionGrain.cs`, `CallSessionGrain.cs`
- `E164.cs`

**New — Backend (`src/Agent42.Backend/Telephony/`, flat `Agent42.Backend` namespace — ADR 0040):**
- `AgentApi.Telephony.cs` (the 13th partial), `TelephonyContracts.cs`
- `TelephonyConfig.cs`, `ICallAutomation.cs`, `AcsCallAutomation.cs`
- `IAcsCallbackValidator.cs`, `AcsCallbackValidator.cs`
- `CallTurnRunner.cs`, `SpeechText.cs`

**Modified — source:**
- `src/Agent42.Agents/StoredAgents/AgentDefinition.cs` — trailing `[Id(10)]`
- `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs` — per-operation directory sync
- `src/Agent42.Agents/Conversations/IAgentGrain.cs` + `Conversations/AgentGrain.cs` — trailing
  `bool interactive = true` on `ChatAsync`
- `src/Agent42.Backend/Api/AgentApi.cs` — register `MapTelephonyEndpoints`
- `src/Agent42.Backend/Program.cs` — telephony registrations
- `src/Agent42.Backend/Configuration/ConfigHealth.cs` — telephony rows
- `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` +
  `src/Agent42.Backend/StoredAgents/AgentContracts.cs` *(the real filename — there is no
  `StoredAgentContracts.cs`)*
- `src/Agent42.Backend/wwwroot/js/agents.js`, `index.html`, `sw.js` (`CACHE` bump v19 → v20)
- **`infra/main.bicep`** — declares the new optional params and threads them to the module. **This
  is the file the previous revision forgot, and the omission breaks the entire deploy, not just
  telephony:** every optional secret is declared here (`googleMapsApiKey`, `contextApiKey`,
  `vapidPrivateKey`) and passed through to `container-app.bicep`; passing
  `acsConnectionString=…` on the `az deployment group create` line for a template that does not
  declare it is an ARM `InvalidTemplate` failure. Step 1's `az bicep build` **cannot** catch it,
  because a `main.bicep` that never passes the params still compiles.
- `infra/modules/container-app.bicep` — optional secrets + `excludedPaths` entry
- `.github/workflows/deploy.yml`
- `Directory.Packages.props`, `src/Agent42.Backend/Agent42.Backend.csproj`

**Deliberately NOT created:** `infra/modules/communication.bicep`. The ACS resource lives in a
**separate Pay-As-You-Go subscription** (see the Step 0 precondition) and is created by hand there;
the backend consumes only its connection string. That removes a Bicep module, a cross-subscription
deployment target, and any need for the deploy SP to hold rights outside `rg-agent42`.

**New — tests (`tests/Agent42.Tests/`, flat, ADR 0039):** `TelephonyApiTests.cs`,
`TelephonyTurnTests.cs`, `SpeechTextTests.cs`, `AcsCallbackValidatorTests.cs`, `E164Tests.cs`,
`PhoneNumberDirectoryGrainTests.cs`, `CallSessionGrainTests.cs`.

**Modified — tests that hold hard-coded exact lists and WILL fail otherwise:**
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — `ExpectedFolders()` asserts the *exact* folder set
  of both projects by name (`FeatureFolderSet_IsExactlyWhatTheAdrDescribes`), and `AgentApiParts()`
  is a hard-coded 12-row `TheoryData` whose doc comment says "The twelve endpoint groups" and needs
  the `Telephony` row **and** that comment updated.
- `tests/Agent42.Tests/GrainStorageSerializerTests.cs` — `PersistedTypeNames()` is a literal list of
  every persisted state root; add `PhoneNumberDirectoryState`.
- `tests/Agent42.Tests/GoldenStates.cs` + `SerializationCompatibilityTests.cs` — a golden blob is
  required for every persisted grain-state root.
- `tests/Agent42.Tests/EasyAuthExclusionTests.cs`, `PwaAssetsTests.cs`, `ConfigHealthTests.cs`,
  `AgentEditorAssetsTests.cs`, `AgentRegistryGrainTests.cs`, `AgentGrainTests.cs`.

**Modified — docs (hard rule 2: fixed in the same MR):**
- `.agentic/decisions/0002-entra-easy-auth-gate.md` — its amendment header enumerates "**four
  carve-outs**"; this makes it five.
- `.agentic/gotchas.md` — the `excludedPaths` entry says "There are now **four** entries"; plus the
  new gotchas from Step 8.
- `CLAUDE.md` — the ADR 0035 bullet says "**The four entries are** …"; plus the ADR 0039/0040
  folder counts and a new ADR 0048 bullet. **Re-derive those counts from
  `ProjectLayoutTests.ExpectedFolders()` rather than incrementing the prose** — the two already
  disagree (the Agents list carries 17 names against CLAUDE.md's "16"; the Backend list carries 19
  including the non-feature `Properties`/`wwwroot` against its "17"), so blindly adding one would
  cement the drift that test exists to catch.
- New `.agentic/decisions/0048-*.md`.

## Context & constraints

### Decisions the user made (do not re-litigate)

- **D1 — Turn-based, and the agent stays the brain.** ACS `Recognize`/`Play` over HTTPS webhooks,
  feeding the real `AgentGrain`. Rejected: any architecture where a separate speech-to-speech model
  owns the conversation, because the Agent Framework pipeline (RAG `TextSearchProvider`, MCP
  `AITool`s, `AgentSkillsProvider`, the ADR 0003 persona snapshot) does not carry over. **This is
  what rules out Azure Voice Live — see F11.**
- **D2 — Caller ID only; no PIN.** The user was shown that PSTN caller ID is trivially spoofable
  and that a spoofed call reaches an agent holding their tools, location, MCP servers and history,
  and chose caller-ID-only anyway. **Implement it as chosen** and record it as an accepted residual
  risk in the ADR — do not add a PIN gate. Two properties partially offset it and *are* in scope:
  ACS rejects a forged `incomingCallContext`, so a fabricated Event Grid event cannot make a call
  happen; and every call is a listed conversation, so an unauthorized call is visible afterwards.
  **A third is mandatory because of D2:** the caller's number *is* the credential, so the directory
  must be claim-once (F2) — otherwise a second user can silently take over a number.
- **D3 — New conversation per call.** Each call mints its own listed conversation. Rejected: one
  continuous phone thread (ADR 0016 shape → unbounded growth into the 2 MB cap, ADR 0004) and
  ephemeral (ADR 0034 — nothing to read back).
- **D4 — ACS on a separate Pay-As-You-Go subscription.** Agent42's own subscription is MSDN-backed,
  which cannot buy a phone number (Step 0). The PAYG subscription sits in the **same Entra tenant**,
  so the residency property that motivated choosing ACS at all is fully preserved. Rejected:
  converting the main subscription to PAYG (loses the monthly credit everywhere) and switching to
  Twilio/Telnyx (call audio would leave the tenant — see the ADR 0048 provider comparison).

### Findings from the adversarial review that changed the design

Caught by the `critic` pass; load-bearing, do not simplify back out.

- **F1 — A call must `BindAgentAsync` before its first turn.** Both existing agent entrypoints do it
  (`src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` for the ADR 0022 agent-run;
  `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` for the browser). Without it the call
  runs on `_chatClientFactory.DefaultModelId` with the *user's* default tools, no system prompt and
  no ADR 0003 snapshot — i.e. it is not the agent at all, which is this feature's entire point. It
  also breaks the ADR 0031 `application` dimension (`PublishUsageApplication` falls back when
  `AgentName` is empty) and leaves the model picker unlocked when the conversation is reopened.
- **F2 — The phone directory is NOT a copy of `IAgentKeyDirectoryGrain`'s semantics.** That grain's
  doc comment says overwrite-on-register is safe *because* "a hash collision is cryptographically
  impossible for 256-bit keys, so this only ever re-registers the same owner". A phone number is
  user-supplied and collides by construction. Registration must be **claim-once** (reject when
  already held by a different owner) and deregistration must **verify the owner**. Same file shape,
  deliberately different contract — say so in its doc comment.
- **F3 — Do not hash the phone number; the privacy argument was false.** SHA-256 is right for a
  32-byte random API key (`ApiKey.cs` explains why) and wrong for an E.164 number, which has well
  under 2^30 candidates and is trivially reversed from the digest. And the raw numbers are persisted
  in the `AgentDefinition` document regardless. Use the **normalized E.164 as the grain key** and
  make no privacy claim about it.
- **F5 — Answer the callback immediately; run the turn in the background.** A turn is seconds and
  unbounded with RAG/MCP/tools. ACS retries a callback that times out or answers non-2xx, and the
  same `RecognizeCompleted` would replay into a **non-reentrant** `AgentGrain`, producing a
  duplicate persisted turn double-counted on the ADR 0031 dashboard.

  > **CORRECTION — this is NOT `[OneWay]`, and the previous revision was wrong to say so.**
  > `[OneWay]` is an Orleans **grain-method** attribute requiring a `Task`-returning method with no
  > result (`IAgentGrain.EnqueuePromptAsync`, the ADR 0022 exemplar). `CallTurnRunner` is a
  > **Backend class**, and it must *drain* `ChatAsync`'s `IAsyncEnumerable<AgentStreamEvent>` to
  > accumulate the reply — neither of which `[OneWay]` can express. The real mechanism is a
  > **detached task started after the response is written**, which carries hazards `[OneWay]` does
  > not and which the plan must therefore handle explicitly:
  > - The ASP.NET **request scope is disposed** once the response completes. F6 resolves
  >   `ICallAutomation` / `TelephonyConfig` via `IServiceProvider`, so those resolutions must come
  >   from the **root** provider and be **singletons** — a scoped capture would be disposed
  >   mid-turn.
  > - A container-app **restart or scale-in mid-turn silently loses the turn**, leaving the call in
  >   dead air with no pending `Recognize` (see F13). There is no redelivery: ACS already got its
  >   200.
  >
  > If that is unacceptable, the alternative is to move the drain into a real `[OneWay]` grain
  > method as the exemplar does — but the grain would then need to reach ACS, which inverts the
  > Agents/Backend dependency direction (ADR 0039/0040). **Take the detached task and record the
  > shutdown hazard**; do not claim `[OneWay]`.

  Dedupe on the recognize `operationContext` held in the call session so a replay is a no-op — see
  F14 for what that actually requires.
- **F6 — `TelephonyConfig` must NOT be a minimal-API handler parameter.** `MapAgentApi()` is called
  from **12** places (`src/Agent42.Backend/Program.cs`, `tools/Agent42.DevHost/Program.cs`, and ten
  test fixtures). A handler param a host cannot resolve is treated as a body and **breaks routing
  for the whole host** — the documented `IHttpClientFactory` / `PushConfig` trap. Resolve it
  optionally via `IServiceProvider` inside the handler, as for `ICallAutomation`. No host gains a
  new registration.
- **F7 — The ACS JWT validator needs a seam or its test cannot run.** `ValidateTokenAsync` fetches
  the OpenID configuration *before* it can reject anything, so a "reject a bad token" test would
  either hit `acscallautomation.communication.azure.com` from CI or not run. Put an
  `IAcsCallbackValidator` behind `AcsCallbackValidator` (the `IWebPushClient` / `IGeocoder`
  exemplar) and inject the `OpenIdConnectConfiguration` so tests supply a local signing key.
  **Also test the positive direction** — without it the endpoint could 401 on everything and stay
  green.
- **F8 — `RequestContext` carries a `UserTelemetryContext`, not a userId string.** On an
  `excludedPaths` request `UserTelemetryMiddleware` has *already* published one built from absent
  Easy Auth headers (i.e. `local-dev`). The runner must **overwrite** that carrier under
  `UserTelemetryContext.RequestContextKey`. Cite the "user identity reaches downstream/grain spans
  via Orleans `RequestContext`, never `Activity.Baggage`" gotcha.
- **F9 — The callback URI must come from configuration, not the request.** ACS needs an absolute
  HTTPS URI. Deriving it from `Request.Scheme` reproduces the exact bug ADR 0042 fixed for
  `ShareUrl` (`http://` behind the ACA ingress). Add `Telephony:PublicBaseUrl` and build from it.

*(F4 — the `PlayCompleted` correlation rule — is **gone**, dissolved by F10 below rather than fixed.
Its complexity existed only because Play and Recognize were separate actions.)*

### The redesign: reply-as-Recognize-prompt (F10) and why Voice Live is out (F11)

- **F10 — Speak each reply as the `Prompt` of the next `Recognize`.** ACS `Recognize` takes a
  `Prompt` (a `TextSource`), so the agent's reply can *be* that prompt. This buys **one ACS action
  per turn instead of two** and deletes the `PlayCompleted` → re-recognize correlation entirely.
  It also **plausibly eliminates the self-echo hazard** — but mark this as an *inference, not
  evidence*: because `InterruptPrompt` is DTMF-only (see the correction below), speech recognition
  should not begin until the prompt has finished, so the mic is never live while the agent speaks.
  That follows from the parameter semantics; it is **not stated anywhere in the docs**, and this
  repo's rule for load-bearing vendor behaviour is a spike (ADR 0014 is "spike-confirmed", ADR 0038
  "spike-informed"). **Step 0.5 must confirm it.** The contrast still holds either way: with
  separate Play + Recognize, N sentences produce N `PlayCompleted` events and re-arming on the first
  one demonstrably puts the mic up mid-reply — the assistant-hears-itself echo ADR 0037 records as
  its worst residual.

  > **CORRECTION — `InterruptPrompt` is DTMF-only, not speech.** An earlier revision of this plan
  > claimed F10 delivered talk-over-the-agent barge-in. It does not. The parameter table defines
  > `InterruptPrompt` as *"If the participant has the ability to interrupt the playMessage **by
  > pressing a digit**"*, and the concept page's "Interrupt audio prompts" section says *"With
  > **DTMF** interruption your application can allow users to interrupt the flow"*. **ACS Call
  > Automation has no speech barge-in.** Set `InterruptPrompt: true` and
  > `RecognizeInputsType = speechordtmf` anyway — a keypress is a real, if crude, escape hatch from
  > a long reply — but **speech barge-in returns to Out of scope**, and the only routes to it are
  > Azure Voice Live or a third-party voice layer, both of which cost D1 (F11).

  **The accepted cost:** `Recognize` takes its prompt up front, so the reply can no longer be spoken
  sentence-by-sentence as it generates; the caller hears nothing during model generation. This is
  still the right trade — it removes an entire class of correlation bug and the echo hazard — but it
  is now a *simplicity and correctness* trade, not a latency-for-barge-in one. It is why
  `SentenceSplitter` is replaced by the much simpler `SpeechText`.
  **Recorded future option, not in scope:** a hybrid that `Play`s the first sentence immediately and
  puts the remainder in the Recognize prompt, recovering the early start at the cost of
  reintroducing two-action correlation.
- **F10b — Recognize parameters must all be set explicitly.** Defaults are hostile to conversation:
  `InitialSilenceTimeout` defaults to 5 s and is **capped at 20 s for Speech**; `EndSilenceTimeout`
  defaults to **0.5 s**, which will cut a caller off mid-sentence at any natural pause. Put these in
  a `CallLimits` block as **named constants beside `MaxRecognizeFailures`**, not inline literals:
  `EndSilence` ≈ 1 s (matching ADR 0037's measured 1.2 s browser debounce), `InitialSilence` near
  the 20 s ceiling, plus `MaxSpokenReplyChars` for the `SpeechText` cap.
  The seam also needs two things the previous revision omitted entirely: the **target participant**
  (the caller's `PhoneNumberIdentifier`, derivable from `CallSessionGrain.callerNumber`) and the
  **speech language** (ADR 0005 stores `preferredLanguage` per user — reuse it, as ADR 0037 does for
  `recognition.lang`).
  **Unverified and worth the spike (Step 0.5):** whether `InitialSilenceTimeout` starts counting
  *during* the prompt or only after it finishes. If during, any reply longer than the timeout raises
  `RecognizeFailed` **while the agent is still speaking**, three of those hang the call up, and the
  feature is dead in prod with every unit test green.
- **F11 — Azure Voice Live cannot host `AgentGrain` as the brain, so it is rejected under D1.**
  Its Bring-Your-Own-Model feature means *any model **deployed in your Azure Foundry resource***;
  the three modes are `byom-azure-openai-realtime`, `byom-azure-openai-chat-completion` and
  `byom-foundry-anthropic-messages`, reached at
  `wss://<foundry-resource>.services.ai.azure.com/voice-live/realtime?profile=…&model=<deployment>`.
  There is **no arbitrary HTTP / OpenAI-compatible endpoint option**. Agent42 could only participate
  as a *function call* or as an *MCP server* behind Voice Live's own model — two LLMs in the loop,
  with the persona, RAG and history stranded behind a tool boundary. Note this is **not** a
  residency or cost objection: Voice Live *is* available in `westeurope` (and Sweden Central, France
  Central, Germany West Central, Italy North, Norway East, Switzerland North, UK South), with the
  GPT-4o/4.1/5 family as *Data zone standard*; and at personal volume every option costs €1–2/month.
  It is purely architectural. **Write this into ADR 0048 so it is not re-proposed.**

- **F13 — A turn that THROWS leaves the call in billed dead air.** Step 7 handles the empty-reply
  case, but not `ChatAsync` throwing: a model 429/413, an MCP timeout, a RAG failure, a
  `NoModelChatClient` state. On a background path the exception is swallowed (the ADR 0022
  precedent: "there is no caller to surface a failure to"), **no `Recognize` is issued**, and the
  four-event dispatch has no timer or fallback — so the call stays up, silent, billed at
  USD 0.0100/min until the caller gives up. F10 makes this *more* likely, not less, because the
  single action is issued only at the very end of the turn. Wrap the turn in `try/catch`, speak a
  curated failure line, and re-arm — or hang up.
- **F14 — The replay dedupe has two unspecified halves, one of which kills the call on turn 2.**
  - **Who mints the `operationContext`, and that it must be unique per turn.** An implementer who
    passes a constant gets: turn 1 works, then every subsequent `RecognizeCompleted` matches
    `LastHandledRecognizeContext` and is skipped — the call answers once and goes permanently
    silent. That is a plausible reading of the previous revision as written.
  - **A single "last" value only dedupes an *immediate* replay.** ACS retries are not ordered; a
    retry of turn N−1 arriving after turn N is recorded does not match, and runs a duplicate turn
    into the non-reentrant grain — the exact harm F5 exists to prevent. Use a **bounded set** of
    handled contexts, or a monotonic sequence number.
  - Separately: **`ConsecutiveRecognizeFailures` must reset on a successful recognize**, or three
    scattered failures across a long call trigger a spurious hang-up.

### Other investigation findings the executor cannot cheaply re-derive

- **The whole feature must be OFF the availability path.** Telephony config follows the
  `contextApiKey`/`vapidPrivateKey` shape, *not* `searxngSecret`: blank connection string → the app
  boots normally, the two endpoints 404, nothing else changes. Evidence: the optional-secret
  `concat()` pattern in the secrets block of `infra/modules/container-app.bicep`, and the
  `@minLength(16)` on `searxngSecret` in `infra/main.bicep`.
- **`excludedPaths` matching is exact unless the entry ends in `/*` (ADR 0042).** The entry must be
  `/api/telephony/*`. A bare `/api/telephony` would match nothing we serve and the feature would be
  silently dead behind the Entra login page in prod while every test passed. `EasyAuthExclusionTests`
  already encodes the measured matcher — extend it, don't reinvent it.
- **An `excludedPaths` endpoint is publicly reachable and must self-enforce and fail closed**, taking
  userId from the directory and **never** from `GetUserId()` — which falls back to the literal
  `local-dev` when the Easy Auth header is absent, i.e. it would hand a public caller a real user's
  grain. Evidence: `src/Agent42.Backend/Api/CurrentUser.cs` (`ResolveUserId`).
- **ACS signs every mid-call webhook with an OIDC JWT.** Issuer
  `https://acscallautomation.communication.azure.com`, OpenID config
  `https://acscallautomation.communication.azure.com/calling/.well-known/acsopenidconfiguration`,
  audience = the ACS resource id, RS256, 5-minute lifetime, new token per event. Validate it
  **inside the endpoint**, **not** via `AddJwtBearer` + `UseAuthentication` middleware — this host
  runs behind Easy Auth and must not grow an auth middleware that could reorder around it.
  *Rejected:* ACS's supported query-parameter token (`?token=…`). Simpler and needs no packages, but
  it puts a long-lived shared secret in a URL — the exact leak channel the ADR 0029 gotcha documents
  for the geocoding key, where it reaches access logs and OTel `http.url`.
- **The Event Grid `IncomingCall` webhook is a different mechanism** and is *not* JWT-signed. It needs
  the `SubscriptionValidationEvent` handshake (echo `validationCode`) at subscription-creation time.
  Ongoing protection is a **static delivery header** carrying a secret (Event Grid
  `deliveryAttributeMappings`) — a header, not a query string, for the reason above.
- **The Event Grid subscription is a manual one-time prerequisite, not Bicep.** Event Grid validates
  the endpoint by calling it at creation time, so a Bicep-created subscription races the container
  app revision becoming healthy on first deploy. This repo already has a manual-prerequisite culture
  (`SEARXNG_SECRET`, the ADR 0025 redirect-URI registration, the RBAC elevation).
- **ACS ↔ Azure AI Services has TWO halves, and the previous revision specified only one.**
  - *The RBAC half:* a **multi-service** Azure AI Services resource linked to the ACS resource via
    the ACS **system-assigned identity** holding **Cognitive Services User** on it. A role
    assignment, so the agent must not execute it (hard rule 5) — prepare the
    `az role assignment create` command and hand it over. Under D4 this is now **cross-subscription
    and cross-resource-group**.
  - *The config half — F12, and it is load-bearing:* ACS will not do text prompts or speech
    recognition unless the **Cognitive Services endpoint URI** is supplied per call
    (`CallIntelligenceOptions.CognitiveServicesEndpoint` on answer/media). It therefore needs a
    fourth setting, **`Telephony:CognitiveServicesEndpoint`**, which must be part of
    `TelephonyConfig.Enabled` — otherwise telephony reports *configured* while **every** `Recognize`
    fails, which is precisely the silent degradation ADR 0029 exists to prevent.
  - *An open Step 0 question, not an implementation detail:* **which** resource this is. The repo's
    only AI resource is the Foundry account in RG `agent42` (`CLAUDE.md`; `azureOpenAIEndpoint` in
    `infra/main.bicep`), reached by API key since ADR 0017. Nothing establishes it is a multi-service
    `AIServices` account **with a custom subdomain**, which is what ACS requires. Confirm this before
    building; if it is not eligible, a new AI Services resource is a fifth manual prerequisite.
- **TTS prompts cap at 4000 characters.** Microsoft's ACS↔AI Services integration doc states
  text-to-speech prompts support a maximum of 4000 characters, and recommends SSML beyond that.
  `SpeechText` must cap the spoken reply well under that; a long reply is truncated with a spoken
  tail like "…there's more, but I'll stop there."
- **`ChatAsync` sets `_interactiveTurn = true`**, which triggers the ADR 0036 follow-up-suggestions
  model call. On a phone turn those chips can never be rendered, so it is a wasted call per turn.
  Add a **trailing optional** parameter, exactly as ADR 0034 added `bool ephemeral = false`. Safe:
  there is exactly one production call site (`AgentApi.Conversations.cs`), every other caller is a
  test using positional args that still bind, `[Alias("ChatAsync")]` keeps the method id stable, and
  `SerializationCompatibilityTests` does not pin interface signatures.
- **The phone path must not send an away-reply push (ADR 0028).** The user is *on the line*. This is
  free if the turn runs through `ChatAsync`, because the push lives only in `EnqueuePromptAsync` and
  `ReceiveReminder` — but it is free *by placement*, which is precisely the property the ADR 0028
  gotcha says to protect. Pin it with the existing `RecordingPushNotificationSender`.
- **Per-operation directory sync, or a revoked binding stays live.** `AgentRegistryGrain` already
  does this for API-key hashes. Phone numbers are a *set*, so update must deregister every
  **removed** number.
- **Trailing `[Id(10)]` on `AgentDefinition` is the zero-migration shape** (`[Id(9)]` is currently
  the last taken), and the guard that matters is the **Cosmos JSON** one: Orleans Cosmos storage
  uses `JsonGrainStorageSerializer` (ADR 0039/0045). Add a pre-change golden document, as ADR 0045
  did for `ConversationInfo.Pinned`.
- **A named `HttpClient` silently inherits `AddServiceDefaults`' retry pipeline.** The ACS SDK brings
  its own transport; take no named client.
- **Every MR touching `wwwroot/js/` must bump `CACHE` in `sw.js`** (currently `agent42-shell-v19`)
  and `SHELL` is all-or-nothing. No new JS *module* is needed here, so `SHELL` is unchanged — the
  bump is still mandatory.
- **Offline (DevHost/AppHost) cannot exercise the real flow.** ACS cannot reach `localhost`; a dev
  tunnel is required for manual verification. Treat the live ACS exchange the way ADR 0020/0038
  treat image-gen and TTS: seam-tested offline, prod-smoke verified. Do **not** invent an offline
  fake ACS in DevHost.

### Packages

Resolve versions with tooling at add time, never from memory (hard rule 4); pin in
`Directory.Packages.props` under Central Package Management.
- `Azure.Communication.CallAutomation` (a stable GA version exists — confirmed on nuget.org at plan time)
- `Azure.Messaging.EventGrid` (for `EventGridEvent.Parse` + `SubscriptionValidationEventData`)
- `Microsoft.IdentityModel.Protocols.OpenIdConnect` + `Microsoft.IdentityModel.JsonWebTokens`

### Governing ADRs / gotchas to read first

`.agentic/decisions/`: 0002 (Easy Auth — **and amend its four-carve-out header**), 0003 (persona
snapshot), 0004 (2 MB cap), 0013 (egress), 0022 (public endpoint + key directory + `[OneWay]`
background run — **the closest prior art**), 0028 (push placement), 0029 (config health + the probe
leak channel), 0030 (no operator model — see the alternative in Step 8), 0031 (usage `application`
dimension), 0034 (turn-scoped flag pattern), 0035 (two-grain directory split), 0037 + 0038 (voice),
0039/0040 (layout, flat namespaces, folder counts), 0042 (`excludedPaths` wildcards +
`UseForwardedHeaders`/absolute URLs), 0045 (trailing-field + Cosmos JSON golden).

`.agentic/gotchas.md`: "An Easy-Auth `excludedPaths` endpoint is publicly reachable…", "The
away-reply push belongs in the two async callers…", "Config-health: the PROBE path is the leak
channel…", the user-identity-via-`RequestContext` entry, the named-`HttpClient` retry-pipeline
entry, and the `sw.js` `CACHE` entry.

### Cost (measured at plan time, so the ADR can state it)

Netherlands **Local (geographic)** number on ACS: **USD 1.50/month** rental, **USD 0.0100/min**
inbound, billed in 6-second increments. (Toll-free is USD 25.00/month — avoid.) Azure AI Speech STT
and TTS on top, plus the usual model tokens; all in, roughly **USD 0.03/min**, i.e. €1–2/month at
personal volume.

> **Confidence note for the ADR:** the ACS PSTN figures are from Microsoft's published pricing doc
> and are solid. The Azure AI Speech STT (~USD 1/audio-hour) and neural TTS (~USD 16/1M characters)
> figures came from secondary sources because Azure's own speech pricing page renders every value as
> a `$-` placeholder. State them as approximate in ADR 0048; do not present them as quotes.

## Steps

All steps are **ordered**. (Steps 3 and 5 both create `src/Agent42.Agents/Telephony/` and both need
the same `ProjectLayoutTests.ExpectedFolders()` edit, so they are *not* parallelizable.)

### Step 0 — Manual prerequisites (user-run; produce the exact commands, execute none of them)

> **BLOCKING PRECONDITION — settled, but the executor must not undo it.** An ACS phone number
> **cannot** be bought on a Visual Studio / MSDN subscription: eligible agreement types are Modern
> Customer Agreement, Modern Partner Agreement (CSP), Enterprise Agreement and Pay-As-You-Go, and
> *"phone numbers can't be acquired by Azure free credits"*. The trial-number fallback is also
> unavailable, because *"if your Azure billing address is outside of the United States, you are not
> eligible to acquire trial phone numbers"* and this project bills in the Netherlands. Agent42's own
> subscription **is** MSDN-backed. Per **D4** the resolution is a **separate Pay-As-You-Go
> subscription in the same Entra tenant**, holding only the ACS resource — which is why there is no
> `communication.bicep` and why the Cognitive Services role assignment is cross-subscription.

Write these into the ADR and hand the commands to the user:
1. Create the **Pay-As-You-Go subscription** (same Entra tenant), create the **ACS resource** in it
   by hand with `dataLocation: 'europe'` and a **system-assigned identity**, and **purchase a
   Netherlands Local number**.
2. Grant the ACS identity **Cognitive Services User** on the multi-service Azure AI Services
   resource — `az role assignment create …`, **cross-subscription** (hard rule 5: hand over).
3. Create the Event Grid subscription for `Microsoft.Communication.IncomingCall` →
   `https://chat.agent42.nl/api/telephony/incoming` with a static delivery header carrying
   `TELEPHONY_EVENTGRID_SECRET` — `az eventgrid event-subscription create …`.
4. Add GitHub secrets `ACS_CONNECTION_STRING` and `TELEPHONY_EVENTGRID_SECRET`.

**Verify:** none (documentation step). The ADR must list all four **and** the precondition.

### Step 0.5 — Spike the ACS Recognize behaviour before writing Step 2

Use `/spike-dotnet` (a throwaway file-based app outside the repo tree, pinned to the repo's package
versions) against the real ACS resource and the purchased number. **Three questions, all
load-bearing, none answerable from the docs:**

1. Does `InitialSilenceTimeout` start counting **during** the prompt or **after** it finishes?
   (F10b — if during, a long reply self-triggers `RecognizeFailed` and the call hangs up.)
2. Is speech recognition genuinely **inactive while the prompt plays**, i.e. does the agent's own
   audio never appear in `RecognizeCompleted`? (F10's echo claim, currently an inference.)
3. With `RecognizeInputsType = speechordtmf` and `InterruptPrompt: true`, does a **DTMF keypress**
   stop the prompt and return promptly? (The only interruption ACS offers.)

**If (1) or (2) comes back badly**, the fallback is the recorded hybrid — `Play` the reply, then
`Recognize` with a short "go ahead" prompt — which reinstates the two-action correlation this design
removed. Record the measured answers in ADR 0048 with a date, in the ADR 0014 / ADR 0038 style.

**Verify:** the spike's own output, quoted into the ADR. Do not proceed to Step 2 without it.

### Step 1 — Infra: optional secrets and the Easy Auth carve-out

Files: **`infra/main.bicep`**, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml`.

- **`infra/main.bicep` first** (B3): declare three **optional** params (`default ''`, **no**
  `@minLength`) — `acsConnectionString`, `telephonyEventGridSecret`,
  `telephonyCognitiveServicesEndpoint` — and thread them into the `containerApp` module exactly as
  `googleMapsApiKey` / `contextApiKey` / `vapidPrivateKey` already are. Omitting this file makes the
  whole deploy fail with `InvalidTemplate`, not just telephony.
- In `container-app.bicep`, wire the two secrets through the `concat()` optional-secret pattern to
  `Telephony__ConnectionString` and `Telephony__EventGridSecret`, and the non-secret
  `Telephony__CognitiveServicesEndpoint` (F12) as a plain env var.
- Also set `Telephony__PublicBaseUrl` (F9) — **derive it from the existing `customDomainName`
  param**, not a new literal. A wrong value here reproduces the ADR 0042 silent-death shape and only
  the Step 8 health row would surface it.
- Add **`/api/telephony/*`** (with the trailing `/*`) to `excludedPaths`.
- Pass all three new parameters in the `az deployment group create` list.
- **No ACS resource module** (D4).

**Verify:** `az bicep build --file infra/main.bicep` **and** confirm by reading that `main.bicep`
actually passes each new param through — the build alone cannot catch a param that is declared,
defaulted and never used (B3). Then `dotnet test Agent42.slnx --filter EasyAuthExclusionTests`.

### Step 2 — Telephony config + ACS client and validator seams

Files: `src/Agent42.Backend/Telephony/TelephonyConfig.cs`, `ICallAutomation.cs`,
`AcsCallAutomation.cs`, `IAcsCallbackValidator.cs`, `AcsCallbackValidator.cs`, `SpeechText.cs`;
`src/Agent42.Backend/Program.cs`; `Directory.Packages.props`; csproj.

- `TelephonyConfig` binds `Telephony:*` and exposes `Enabled` — the `PushConfig.Enabled` shape.
  **`Enabled` requires a non-blank connection string AND a non-blank `CognitiveServicesEndpoint`
  (F12)**; with the endpoint missing, every `Recognize` fails while the feature reports healthy.
  Never echo the connection string. Also holds the `CallLimits` constants (F10b).
- **F6:** `TelephonyConfig` is **not** a handler parameter. Register it in
  `src/Agent42.Backend/Program.cs` only, and resolve it — like `ICallAutomation` and
  `IAcsCallbackValidator` — **optionally via `IServiceProvider`** inside the handler. No other host
  changes, and none of the 12 `MapAgentApi()` call sites is touched.
- `ICallAutomation` is the ACS seam: `AnswerAsync` (carrying the F12 Cognitive Services endpoint),
  `RejectAsync`, **`RecognizeWithPromptAsync`** — prompt text + **target participant** + `language` +
  `operationContext` + the F10b `CallLimits` timeouts + `RecognizeInputsType = speechordtmf` and
  `InterruptPrompt: true` for the DTMF escape hatch — and `HangUpAsync`. There is deliberately **no
  bare `PlayAsync`** (F10). Take no named `HttpClient`.
- **Register `ICallAutomation` and `TelephonyConfig` as singletons** resolved from the root
  provider, never scoped — the turn outlives the request scope (F5's correction).
- **F7:** `IAcsCallbackValidator` wraps JWT validation and takes its `OpenIdConnectConfiguration` as
  a dependency so tests can supply a locally-minted signing key.
- `SpeechText` prepares a reply for TTS: strip markdown, collapse whitespace, and **cap well under
  the 4000-character prompt limit**, appending a spoken truncation tail when it cuts. (This replaces
  the `SentenceSplitter` of the previous revision, which F10 made unnecessary.)

- **Add `src/Agent42.Backend/Telephony/` to `ProjectLayoutTests.ExpectedFolders()` in THIS step**
  (M6). That test asserts the exact folder set of both projects, so it goes red the moment this
  folder exists — the previous revision deferred the edit to Step 3 and would have left the suite
  broken in between.

**Verify:** `dotnet build Agent42.slnx`;
`dotnet test Agent42.slnx --filter "SpeechTextTests|AcsCallbackValidatorTests|ProjectLayoutTests"`.

### Step 3 — Caller directory + per-agent phone numbers

Files: `src/Agent42.Agents/Telephony/E164.cs`, `IPhoneNumberDirectoryGrain.cs`,
`PhoneNumberDirectoryGrain.cs`; `StoredAgents/AgentDefinition.cs`; `StoredAgents/AgentRegistryGrain.cs`;
`src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` + `StoredAgents/AgentContracts.cs`;
`tests/Agent42.Tests/ProjectLayoutTests.cs`, `GrainStorageSerializerTests.cs`, `GoldenStates.cs`.

- `E164.Normalize` — strip spaces/dashes/parens, require a leading `+` and digits, reject otherwise.
  **F3: no hashing.** The grain key is the normalized E.164.
- `IPhoneNumberDirectoryGrain : IGrainWithStringKey`, keyed by the normalized number, riding
  `agent-state` on state `"phone-directory"`. **F2: claim-once** —
  `TryRegisterAsync(userId, agentId)` returns `false` when the number is already held by a different
  owner; `DeregisterAsync(userId)` verifies the owner. Its doc comment must state explicitly that
  this contract **differs** from `IAgentKeyDirectoryGrain`'s overwrite-on-register, and why.
- `AgentDefinition` gains trailing `[Id(10)] IReadOnlyList<string>? PhoneNumbers = null`, documented
  in the existing `<remarks>` style with the zero-migration rationale.
- `AgentRegistryGrain`: create claims each number; **update deregisters every REMOVED number first**,
  then claims the current set; delete deregisters all. A number already claimed by someone else must
  surface as a **409**, not a silent drop.
- Add `PhoneNumberDirectoryState` to `GrainStorageSerializerTests.PersistedTypeNames()` and a golden
  blob to `GoldenStates.cs`; add the two new folders to `ProjectLayoutTests.ExpectedFolders()`.

**Verify:** `dotnet test Agent42.slnx --filter "PhoneNumberDirectoryGrainTests|E164Tests|AgentRegistryGrainTests|GrainStorageSerializerTests|SerializationCompatibilityTests|ProjectLayoutTests"`.

### Step 4 — Agent editor: phone numbers field

Files: `src/Agent42.Backend/wwwroot/index.html`, `js/agents.js`, `sw.js` (`CACHE` v19 → v20),
`styles.css` if needed.

A comma-separated input in the existing agent editor, beside the ADR 0022 API-key controls; surface
the 409 from Step 3 as a visible error. No new JS module (so `SHELL` is unchanged), but **bump
`CACHE`**.

**Verify:** `dotnet test Agent42.slnx --filter "AgentEditorAssetsTests|PwaAssetsTests"`.

### Step 5 — Call session grain

Files: `src/Agent42.Agents/Telephony/ICallSessionGrain.cs`, `CallSessionGrain.cs`.

Keyed by `callConnectionId`. **Deliberately non-persisted** — no `[PersistentState]`, so the session
lives only in the activation and leaves no trace at rest, while still being cluster-routed (an
in-memory dictionary in the endpoint would break the moment the app scales past one replica).

Holds `(userId, agentId, conversationId, callerNumber, startedAt)` plus:
- **`ConsecutiveRecognizeFailures`** — hang up at `CallLimits.MaxRecognizeFailures = 3`, a named
  constant, not an unstated N. **Reset to zero on every successful recognize** (F14), or three
  scattered failures across a long call hang it up spuriously.
- **`HandledRecognizeContexts`** — a **bounded set**, not a single "last" value (F14): ACS retries
  are unordered, so a retry of turn N−1 arriving after turn N must still be recognised as a replay.
- **`TurnSequence`** — a monotonic counter the runner uses to mint a **unique per-turn**
  `operationContext` (F14). A constant context would make the call answer once and then go
  permanently silent.

`EndAsync()` calls `DeactivateOnIdle()`.

*(The previous revision's `PendingPlayContext` is gone — F10 removed the `PlayCompleted` branch it
existed to correlate.)*

**Verify:** `dotnet test Agent42.slnx --filter CallSessionGrainTests`.

### Step 6 — The two public endpoints

Files: `src/Agent42.Backend/Telephony/AgentApi.Telephony.cs`, `TelephonyContracts.cs`;
`src/Agent42.Backend/Api/AgentApi.cs`; `tests/Agent42.Tests/ProjectLayoutTests.cs`.

- `POST /api/telephony/incoming` — Event Grid. Handle `SubscriptionValidationEvent` (echo
  `validationCode`) — but **require the delivery-header secret on that branch too** (m4): Event Grid
  sends static delivery attributes on the validation event, so the exemption the previous revision
  carved out was unnecessary and made AC2 pass with no secret at all. Require the static
  delivery-header secret on every branch (constant-time compare);
  resolve `from` → `E164.Normalize` → directory. **Unknown → `RejectAsync` and stop.** Known → mint a
  conversation id, create the call session, **`BindAgentAsync` with the stored agent's binding (F1)**,
  then `AnswerAsync` with the callback URI built from `Telephony:PublicBaseUrl` (F9). Take userId
  from the **directory**, never `GetUserId()`.
- `POST /api/telephony/callback` — validate via `IAcsCallbackValidator`; 401 on anything else.
  Dispatch on **four** event types (there is no `PlayCompleted` branch — F10):
  - `CallConnected` → `RecognizeWithPromptAsync(greeting)` with the F10b timeouts.
  - `RecognizeCompleted` → **record the `operationContext` in the bounded set, reset the failure
    counter, return 200 immediately, and start the turn as a detached task** (F5's correction — not
    `[OneWay]`), skipping outright if that context was already handled.
  - `RecognizeFailed` → re-prompt with a **short fixed line** (`CallPrompts.StillThere`, e.g. "Sorry,
    I didn't catch that — are you still there?"), **never by replaying the previous reply**, which
    under F10 is the whole last answer (m2); increment the counter; hang up at
    `MaxRecognizeFailures`.
  - `CallDisconnected` → end the session.
- Both endpoints **404 when telephony is unconfigured**, and fail closed on every error path.
- Register `MapTelephonyEndpoints` in the composition root **and add the `Telephony` row to
  `ProjectLayoutTests.AgentApiParts()`** — without that row AC20 proves nothing.

**Verify:** `dotnet test Agent42.slnx --filter "TelephonyApiTests|ProjectLayoutTests"`.

### Step 7 — The turn: transcript → grain → spoken reply

Files: `src/Agent42.Backend/Telephony/CallTurnRunner.cs`;
`src/Agent42.Agents/Conversations/IAgentGrain.cs`; `src/Agent42.Agents/Conversations/AgentGrain.cs`.

- Add trailing `bool interactive = true` to `ChatAsync` (interface and implementation), assigning
  `_interactiveTurn = interactive`. Leave the `finally` clears exactly as they are.
- **F8:** before touching the grain, **overwrite** `RequestContext[UserTelemetryContext.RequestContextKey]`
  with a `UserTelemetryContext` built from the directory-resolved user — the middleware has already
  published a `local-dev` one for this unauthenticated request.
- `CallTurnRunner`: `ChatAsync(transcript, model: null, interactive: false)`, accumulate every
  `TextDelta` into the **complete** reply, ignore `Image` and `Suggestions`, run it through
  `SpeechText`, then issue **one** `RecognizeWithPromptAsync(reply)` — which speaks the reply and
  then listens for the next utterance (F10). Recognition starts only after the prompt finishes, so
  the agent never hears itself.
- Call `ConversationIndexGrain.TouchAsync` with the agent identity on **every** turn — it is
  idempotent for an existing id, and only the creating touch sets the title and agent identity, so
  no "is this the first turn?" flag is needed (m1; the session state has no turn counter to answer
  that question anyway).
- **Wrap the whole turn in `try/catch` (F13).** On failure speak a curated line
  (`CallPrompts.Trouble`) and re-arm, or hang up — never fall through, or the caller sits in silent
  billed dead air.
- An empty reply must still re-arm recognition, for the same reason.
- Do **not** call `SendAwayReplyPushAsync` anywhere on this path.

**Verify:** `dotnet test Agent42.slnx --filter "TelephonyTurnTests|AgentGrainTests"`.

### Step 8 — Config health, docs, gates

Files: `src/Agent42.Backend/Configuration/ConfigHealth.cs`; `.agentic/decisions/0048-*.md`
(via `/decide`); `.agentic/decisions/0002-entra-easy-auth-gate.md`; `.agentic/gotchas.md`
(via `/gotcha`); `CLAUDE.md`.

- **Four** `GroupOptional` rows via `PresenceCheck`: `Telephony:ConnectionString`,
  `Telephony:EventGridSecret`, `Telephony:PublicBaseUrl`, and **`Telephony:CognitiveServicesEndpoint`
  (F12)** — the last is the input most likely to be wrong and the one whose absence is otherwise
  invisible, which is exactly ADR 0029's purpose. **Static only — add no probe** (a probe would
  place a billable ACS call, and the curated-message discipline is easier to get wrong than to skip).
- **Pay the doc debt (hard rule 2):** amend ADR 0002's "four carve-outs" header to five; correct the
  gotchas "There are now four entries" line; correct CLAUDE.md's "The four entries are …"; and
  reconcile the ADR 0039/0040 folder counts against `ProjectLayoutTests.ExpectedFolders()` (see
  Affected areas — the prose and the list already disagree, so re-derive rather than increment).
- **ADR 0048 records:**
  - D1–D4, including **D4's MSDN subscription-eligibility precondition** — the one input that can
    invalidate the provider choice outright rather than merely delay it.
  - **The provider comparison and why ACS won:** tenant residency, NL Local at USD 1.50/mo +
    USD 0.010/min, no local-address regulatory step. Runners-up and what would flip it — Twilio +
    ConversationRelay (+USD 0.07/min, sub-0.5 s latency, barge-in handled, WebSocket speaks **text**
    so the grain stays the brain) and Telnyx (cheapest per minute). Both were rejected because call
    audio would leave the Entra tenant (ADR 0013).
  - **F11 — why Azure Voice Live is rejected**, with the BYOM detail spelled out, so nobody
    re-proposes it: it is an *architectural* rejection under D1, **not** a residency or cost one
    (`westeurope` is supported; costs are within noise of the chosen path). **State the standing
    trade explicitly**, because it is the thing a future reader will want: Voice Live is the
    supported way to get speech barge-in on the phone, and the price is that the conversational
    brain stops being `AgentGrain`. If phone barge-in ever outranks D1, that is the ADR to write.
  - **F10's trade** — one action per turn and no self-echo, paid for with no sentence-streaming —
    the Step 0.5 measured answers (with a date), and the recorded hybrid that would recover the
    early start. Plus **two costs the earlier revision left unrecorded**: caller speech during model
    generation is **lost, not merely unheard** (no `Recognize` is armed between `RecognizeCompleted`
    and the next action, so "hello?" during a slow RAG turn is dropped); and interruption here is
    strictly weaker than ADR 0037's, because the browser aborts generation mid-stream (`sendAbort`)
    whereas here the reply is **already complete and persisted in the session** when a keypress cuts
    it off — so the model's history contains words the caller never heard, and the next turn is
    answered as though they had. Both are accepted residuals.
  - **Self-echo on a speakerphone as an accepted residual**, matching ADR 0037's honesty about
    `isSelfOrEcho` and "headphones remain the clean guarantee" — pending the Step 0.5 answer to
    whether ACS suppresses its own prompt audio.
  - **A side effect worth one line:** `BindAgentAsync` calls `WriteStateAsync`, so a call answered
    and hung up before the first utterance leaves a persisted, never-listed conversation document.
  - **The corrected `InterruptPrompt` reading (F10)**, recorded as a *finding*, not silently fixed:
    an earlier revision of this plan asserted speech barge-in from `InterruptPrompt` and was wrong.
    Writing it down stops the next reader making the same inference from the same parameter name.
  - The four manual prerequisites; the measured costs **with the confidence note above**; and the
    **caller-ID-spoofing accepted residual risk (D2)** together with the claim-once mitigation (F2).
  - The **ADR 0013 egress extension**: call audio now transits ACS and Azure AI Speech (both
    first-party, in-tenant), and the transcript reaches whatever model the agent is bound to.
- **New gotchas:** (a) the phone path must never grow a push (ADR 0028 placement); (b) the phone
  directory is claim-once and is **not** `IAgentKeyDirectoryGrain`'s overwrite contract (F2);
  (c) the reply is the **`Recognize` prompt**, never a separate `Play` — a well-meaning refactor
  back to Play-then-Recognize silently reintroduces the ADR 0037 self-echo, because recognition
  would start while the agent is still speaking (F10); and (d) **`InterruptPrompt` is DTMF-only** —
  the name reads like speech barge-in and is not, so do not build a talk-over feature on it (F10).

**Verify:** `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
`dotnet build Agent42.slnx`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | `/api/telephony/*` (wildcard form) is in `excludedPaths`, and the bare form is proven not to match | `Exclusions_CoverTheTelephonyWebhooks` | `EasyAuthExclusionTests.cs` |
| AC2 | Event Grid subscription-validation handshake echoes `validationCode` | `IncomingCall_AnswersTheSubscriptionValidationHandshake` | `TelephonyApiTests.cs` |
| AC3 | Missing/incorrect Event Grid delivery-header secret → 401, no ACS call issued | `IncomingCall_FailsClosedWithoutTheDeliverySecret` | `TelephonyApiTests.cs` |
| AC4 | Unknown caller number → call rejected, no conversation created | `IncomingCall_RejectsAnUnknownCaller` | `TelephonyApiTests.cs` |
| AC5 | Known caller → answered under the **directory's** userId (never `local-dev`) | `IncomingCall_AnswersAKnownCallerAsTheDirectoryOwner` | `TelephonyApiTests.cs` |
| AC5b | **(F1)** The call `BindAgentAsync`es the stored agent, so the turn runs on the agent's model, system prompt and tool set | `IncomingCall_BindsTheStoredAgentBeforeTheFirstTurn` | `TelephonyApiTests.cs` |
| AC5c | **(F9)** The callback URI is absolute `https` from `Telephony:PublicBaseUrl`, not `Request.Scheme` | `IncomingCall_UsesTheConfiguredHttpsCallbackUri` | `TelephonyApiTests.cs` |
| AC6 | An unsigned / wrong-audience / expired token → 401, **with no network call** | `Validator_RejectsATokenThatIsNotAcs` | `AcsCallbackValidatorTests.cs` |
| AC6b | **(F7)** A correctly-signed ACS token with the right audience is **accepted** | `Validator_AcceptsAGenuineAcsToken` | `AcsCallbackValidatorTests.cs` |
| AC7 | Both endpoints 404 when telephony is unconfigured | `TelephonyEndpoints_AreAbsentWhenUnconfigured` | `TelephonyApiTests.cs` |
| AC7b | **(F6)** Every host that maps `MapAgentApi` still routes with telephony unregistered | existing fixtures pass unchanged + `MapAgentApi_RoutesWithoutTelephonyRegistrations` | `TelephonyApiTests.cs` |
| AC8 | A transcript reaches the grain and the reply comes back | `Turn_BridgesTranscriptToTheAgentAndBack` | `TelephonyTurnTests.cs` |
| **AC9** | **(F10)** The reply is delivered as the **`Prompt` of one `Recognize` action** | `Turn_SpeaksTheReplyAsTheRecognizePrompt` | `TelephonyTurnTests.cs` |
| **AC9g** | **(M3)** "No bare `Play` is ever issued" is asserted by **source text, not the fake** — `ICallAutomation` declares no `Play*` member **and** `AcsCallAutomation.cs` contains no `PlayAsync(` call. A fake cannot record a method the interface does not declare, so the previous phrasing could not fail; meanwhile `CallMedia.PlayAsync` stays reachable from inside the implementation | `NoPlaySinkExists` | `ProjectLayoutTests.cs` |
| **AC9b** | **(F10b)** Every recognize sets `EndSilenceTimeout` ≈ 1 s, `InitialSilenceTimeout` within the 20 s speech ceiling, and `RecognizeInputsType = speechordtmf` | `Recognize_UsesConversationalTimeouts` | `TelephonyTurnTests.cs` |
| AC9c | **(F5)** A replayed `RecognizeCompleted` with an already-handled `operationContext` produces no second turn | `Callback_IsIdempotentOnARepeatedRecognizeEvent` | `TelephonyApiTests.cs` |
| AC9d | **(F5)** The callback responds without waiting for the turn to finish | `Callback_RespondsBeforeTheTurnCompletes` | `TelephonyApiTests.cs` |
| **AC9e** | A reply longer than the TTS limit is truncated under 4000 chars with a spoken tail | `SpeechText_CapsALongReply` | `SpeechTextTests.cs` |
| **AC9f** | An empty reply still re-arms recognition (the call must not go silent) | `Turn_ReArmsOnAnEmptyReply` | `TelephonyTurnTests.cs` |
| **AC9h** | **(F13)** A turn that **throws** speaks a curated line and re-arms — never silent dead air on a metered call | `Turn_ReArmsWhenTheTurnThrows` | `TelephonyTurnTests.cs` |
| **AC9i** | **(F14)** Each turn mints a **unique** `operationContext`, so turn 2 is not skipped as a replay | `Turn_MintsAUniqueContextPerTurn` | `TelephonyTurnTests.cs` |
| **AC9j** | **(F14)** An **out-of-order** replay of an older turn's context is still skipped (bounded set, not last-value) | `Callback_SkipsAnOutOfOrderReplay` | `TelephonyApiTests.cs` |
| **AC9k** | **(F12)** `Enabled` is false when the Cognitive Services endpoint is blank, even with a connection string present | `Telephony_IsDisabledWithoutTheCognitiveServicesEndpoint` | `ConfigHealthTests.cs` |
| AC10 | **No away-reply push fires on the phone path** | `Turn_NeverSendsAnAwayReplyPush` | `TelephonyTurnTests.cs` |
| AC11 | `interactive: false` suppresses the ADR 0036 follow-ups model call | `ChatAsync_SkipsSuggestionsWhenNotInteractive` | `AgentGrainTests.cs` |
| AC12 | *(default-on behaviour is already pinned by the existing `ChatAsync_WhenSuggestionsEnabled_EmitsUpToThreeFollowUps` — assert it still passes unchanged; write no duplicate)* | existing | `AgentGrainTests.cs` |
| AC13 | The call is a listed conversation carrying the agent identity | `Turn_ListsTheCallAsAnAgentConversation` | `TelephonyTurnTests.cs` |
| AC13b | **(F8)** The turn's spans carry the directory-resolved user, not `local-dev` | `Turn_OverwritesTheTelemetryUserCarrier` | `TelephonyTurnTests.cs` |
| AC14 | Directory claim/resolve/deregister round-trips | `ClaimAndResolve_RoundTrips` | `PhoneNumberDirectoryGrainTests.cs` |
| AC14b | **(F2)** A number already claimed by another owner is **refused**, and deregister by a non-owner is a no-op | `Claim_RefusesANumberHeldByAnotherOwner` | `PhoneNumberDirectoryGrainTests.cs` |
| AC14c | **(F2)** The API surfaces a claimed number as 409 | `CreateAgent_ConflictsOnAClaimedNumber` | `TelephonyApiTests.cs` |
| AC15 | Editing an agent **deregisters every removed number** and keeps the rest live | `Update_DeregistersOnlyTheRemovedNumbers` | `AgentRegistryGrainTests.cs` |
| AC16 | Deleting an agent deregisters all its numbers | `Delete_DeregistersEveryNumber` | `AgentRegistryGrainTests.cs` |
| AC17 | E.164 normalization accepts real forms and rejects malformed input | `Normalize_AcceptsAndRejects` | `E164Tests.cs` |
| AC18 | A pre-change `AgentDefinition` **Cosmos JSON** document still binds (zero migration) | `AgentDefinition_BindsAPrePhoneNumbersDocument` | `GrainStorageSerializerTests.cs` |
| AC18b | `PhoneNumberDirectoryState` is listed as a persisted root and has a golden blob | `PersistedTypeNames` row + golden | `GrainStorageSerializerTests.cs`, `SerializationCompatibilityTests.cs` |
| AC19 | The call session holds state without persisting it, counts recognize failures to `MaxRecognizeFailures`, and deactivates on end | `Session_HoldsCountsAndEndsWithoutPersisting` | `CallSessionGrainTests.cs` |
| **AC19c** | **(F14)** A successful recognize **resets** `ConsecutiveRecognizeFailures`, so scattered failures never accumulate to a spurious hang-up | `Session_ResetsFailuresOnSuccess` | `CallSessionGrainTests.cs` |
| AC19b | `RecognizeFailed` re-prompts, then hangs up at the limit | `Callback_HangsUpAfterRepeatedRecognizeFailures` | `TelephonyApiTests.cs` |
| AC20 | `MapTelephonyEndpoints` is registered from the composition root — **requires adding the `Telephony` row to `AgentApiParts()`** | `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` | `ProjectLayoutTests.cs` |
| AC20b | Both new `Telephony/` folders are added to `ExpectedFolders()` | `FeatureFolderSet_IsExactlyWhatTheAdrDescribes` | `ProjectLayoutTests.cs` |
| AC21 | Config health reports telephony presence and **never** echoes the connection string | `Telephony_IsReportedWithoutLeakingTheSecret` | `ConfigHealthTests.cs` |
| AC23 | `sw.js` `CACHE` was bumped | existing `CACHE` assertion | `PwaAssetsTests.cs` |
| AC24 | The agent editor exposes the phone-numbers field | `AgentEditor_HasAPhoneNumbersField` | `AgentEditorAssetsTests.cs` |

**Manual (not automatable — ACS cannot reach `localhost`; ADR 0020/0038 precedent):** place a real
call to the purchased number and hold a two-turn conversation; confirm the agent's persona and one
tool call work over the phone; **press a digit mid-reply and confirm the prompt stops** (the DTMF
escape hatch — the only interruption ACS offers, F10); confirm a natural mid-sentence pause does
**not** cut the caller off (the `EndSilenceTimeout` tuning of F10b, which no unit test can prove);
confirm an unknown number is rejected; confirm the call appears as a listed agent conversation.
Record the result honestly in the ADR, **including anything that was not run**.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold |
|---|---|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `dependencies.vulnerable_packages` | 0 (hard block) |
| `dependencies.outdated_packages` | report (soft) |
| `dependencies.add_policy` | latest-stable, resolved at add time |
| Enforcement | local: `soft_warning` · ci: `hard_block` |
| Coverage tooling | coverlet — `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` |

Branch: `feature/phone-call-your-agent`. **Never commit to `main`** (`branches.protected`).

## Out of scope

- **Outbound calls** (the agent calling you) — no `CreateCall`, no scheduling-triggered calls.
- **Realtime bidirectional audio / a media WebSocket**, and **Azure Voice Live** (F11 — it cannot
  host `AgentGrain` as the brain). `app.UseWebSockets()` is not added.
- **Speech barge-in (talking over the agent).** **ACS Call Automation does not support it** —
  `InterruptPrompt` is DTMF-only (F10). What ships is the keypad escape hatch
  (`RecognizeInputsType = speechordtmf`). The only routes to real voice barge-in are Azure Voice
  Live or a third-party voice layer, and **both cost D1** (the agent stops being the brain). ADR
  0048 must state this plainly, because it is the single largest remaining gap between the phone
  experience and the ADR 0037 browser one — which *does* have barge-in, because a browser can run
  the mic and the speaker at once.
- **Sentence-streamed replies.** F10 trades them for one-action simplicity and echo elimination; the
  hybrid that recovers the early start is recorded in ADR 0048 as a future option, not built here.
- **A PIN or any second authentication factor** — the user chose caller-ID-only (D2).
- **SMS**, DTMF menus/IVR, call recording, voicemail, call transfer.
- **Attachments over the phone** — no images, no document ingestion from a call.
- **A Preferences "Phone" tab** — the binding lives in the existing agent editor.
- **Non-agent (plain) chats over the phone** — a call always targets a stored agent.
- **Offline/DevHost emulation of ACS.**
- Changing anything about the ADR 0037/0038 **browser** voice path. (Reusing ADR 0038's
  *server-side* speech model instead of ACS TTS was considered and rejected — recorded in ADR 0048.)

*(Barge-in was out of scope, briefly moved in scope on a mistaken reading of `InterruptPrompt`, and
is now **out** again on the corrected reading — see the CORRECTION box in F10. Only the DTMF escape
hatch ships.)*

## Review

A `critic` subagent reviewed the first draft against the ADRs, gotchas, quality gates and the code.
Verdict: **do not approve as-is** — 5 blocking, 9 major, 4 minor findings. **All 18 were accepted;
none rejected.** A later research round (Voice Live / provider pricing) then produced F10 and F11,
which **dissolved** two of them rather than fixing them.

| Finding | Where it now lives |
|---|---|
| B1 no `BindAgentAsync` — the call would not be the agent | F1, Step 6, AC5b |
| B2 `TelephonyConfig` is a route-matcher dep in **12** host setups | F6, Step 2, AC7b |
| B3 `ProjectLayoutTests` holds two hard-coded exact lists; AC20 was vacuous | Affected areas, Steps 3/6, AC20 + AC20b |
| B4 Play/Recognize loop contradiction (echo loop) | **Dissolved by F10** — one action per turn, no `PlayCompleted` branch at all |
| B5 inline turn in the webhook — ACS replay = duplicate turn | F5, Steps 5/6, AC9c + AC9d |
| M1 SHA-256 of a low-entropy phone number; privacy claim false | F3, Step 3 |
| M2 global directory collision — one user could hijack another's number | F2, Step 3, AC14b + AC14c |
| M3 new persisted root missing from both serialization guards | Affected areas, Step 3, AC18b |
| M4 the JWT test had no seam and no positive case | F7, Step 2, AC6 + AC6b |
| M5 `RequestContext` carries a `UserTelemetryContext`, not a userId | F8, Step 7, AC13b |
| M6 callback URI would be `http://` behind ACA ingress (ADR 0042 bug) | F9, Steps 1/6, AC5c |
| M7 unpaid doc debt: three "four entries" claims + two folder counts | Affected areas, Step 8 |
| M8 recognize-failure counter and limit undefined | Step 5 (`MaxRecognizeFailures`), AC19 + AC19b |
| M9 reintroduces an operator-level AI resource against ADR 0030's stance | Step 8 — recorded as a rejected alternative in ADR 0048 |
| minor: `StoredAgentContracts.cs` does not exist | corrected to `AgentContracts.cs` |
| minor: Steps 3 and 5 are not parallelizable | corrected — all steps ordered |
| minor: AC22 was unfalsifiable | dropped, with the reason recorded |
| minor: AC12 duplicated an existing test | reframed to assert the existing test still passes |

The critic also confirmed clean: every factual claim about the repo it checked; that the trailing
`bool interactive = true` parameter breaks no caller; the verbatim quality-gate table; the
`planned-against` header; and that nothing listed Out of scope is performed by a step.

### Second critic pass (on the F10/F11/D4 revision)

Verdict: **do not approve as-is** — 3 blocking, 7 major, 4 minor. **All accepted.** F11 and D4 came
back clean; the findings clustered on F10, which was the least-evidenced part of the plan.

| Finding | Resolution |
|---|---|
| B1 F10's barge-in claim was unsourced, with no spike and no fallback | **Independently confirmed a real error** — `InterruptPrompt` is DTMF-only. Corrected in F10; **Step 0.5 spike added** with an explicit fallback branch |
| B2 the ACS↔Cognitive-Services **endpoint** config input was missing entirely | F12 — a fourth setting, part of `Enabled`, a config-health row (AC9k), and an open Step 0 question about which resource qualifies |
| B3 `infra/main.bicep` missing from Step 1; its verify could not catch it | Added to Affected areas and Step 1, with the `InvalidTemplate` consequence and a read-the-file verify |
| M1 recognize timeouts, target participant and language unspecified | F10b — named `CallLimits` constants, participant, language; the during-vs-after timing question moved into Step 0.5 |
| M2 `[OneWay]` cannot express this | **Correction box in F5** — detached task, singleton lifetimes, host-shutdown loss stated |
| M3 AC9's "no bare `Play`" unfalsifiable through the seam | Split into AC9g as a source-text assertion in `ProjectLayoutTests` |
| M4 the echo rationale was backwards | Partly **dissolved** by the `InterruptPrompt` correction (the mic is *not* live during the prompt), but the claim is now marked an **inference** and sent to Step 0.5; speakerphone self-echo recorded as a residual |
| M5 a throwing turn leaves billed dead air | F13 + AC9h |
| M6 Step 2 creates a folder but Step 3 held the `ExpectedFolders()` edit | Moved into Step 2, with `ProjectLayoutTests` added to its filter |
| M7 dedupe halves unspecified; failure counter never reset | F14 — unique per-turn context, bounded set, reset-on-success; AC9i, AC9j, AC19c |
| m1 "first turn" not determinable from session state | Every turn calls the idempotent `TouchAsync`; orphan-document side effect recorded |
| m2 `RecognizeFailed` re-prompt text undefined | `CallPrompts.StillThere`, never a replay of the last reply |
| m3 two F10 costs unrecorded | Both added to the ADR 0048 content list |
| m4 missing "New — tests"; `PublicBaseUrl` source; needless validation-event exemption | All three corrected |

**Not re-reviewed:** F12–F14, Step 0.5 and the AC additions above landed after this pass. The plan
has now been through two critic rounds and every finding of both is folded in, but a third pass
before execution would be cheap insurance.
