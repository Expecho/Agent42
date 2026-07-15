# Plan — Multi-provider model catalog (OpenAI-compatible + ApiKeyCredential)

planned-against: main @ 85feb54

## Overview

Today the app can only talk to one place: `AzureOpenAIChatClientFactory` is hardwired to a single
Azure AI Foundry endpoint + `DefaultAzureCredential`, and a model is named by a bare deployment
string (`gpt-4o`). Goal: let a conversation run on **any OpenAI-compatible endpoint authenticated
with an `ApiKeyCredential`** — GitHub Models in production, and (by config alone, no code) an
Ollama or vLLM endpoint for anyone who points at one.

Answering the question that prompted this plan: **yes, a provider/model configuration section is
needed.** Two endpoints reachable by two different credential types, with model names that are no
longer unique across them, is exactly the shape a config section exists to express. Leaving the
Foundry endpoint as a privileged special case would mean two ways to name a model and a factory that
branches on which one it got.

**This routes conversation content to a third party.** That is not a side effect — it is the
feature, and it is the reason this plan carries more security weight than its size suggests. Read
C11 before implementing.

## Affected areas

Verified against the tree at `85feb54`.

| File | Change |
|------|--------|
| `src/Agent42.Agents/IChatClientFactory.cs` | Model-centric vocabulary + a capability query |
| `src/Agent42.Agents/AgentGrain.cs` | `BuildAgent` gates tools/skills on capability; `ChatCoreAsync` strips tool history on a downgrade rehost; attachments gated on vision |
| `src/Agent42.Agents/AgentGrainState.cs` | **Comment only** — the persisted property name does not change (C4) |
| `src/Agent42.Backend/ModelProviderOptions.cs` | **New** — the `Models:` config section + qualified-id parsing |
| `src/Agent42.Backend/ChatClientFactory.cs` | `AzureOpenAIChatClientFactory` → `MultiProviderChatClientFactory` |
| `src/Agent42.Backend/ModelCatalog.cs` | `AzureModelCatalog` composed behind a `CompositeModelCatalog`, **one instance per discovering Foundry provider** and its ARM coordinates injected rather than read from `IConfiguration` (C14); `ModelInfo` gains fields |
| `src/Agent42.Backend/Program.cs` | Bind options; register the composite factory + catalog |
| `src/Agent42.Backend/appsettings.json` | The `Models:` section |
| `src/Agent42.Backend/wwwroot/app.js` | **Four sites** — see Step 7; the composer picker *and* the agent dialog |
| `infra/main.bicep`, `infra/modules/container-app.bicep` | `githubModelsToken` secret → env var |
| `.github/workflows/deploy.yml` | Pass the `GITHUB_MODELS_TOKEN` secret |
| `tools/Agent42.DevHost/Program.cs` | Static catalog uses qualified ids; a no-tools model to exercise the degraded path |
| `tests/Agent42.Tests/TestSiloConfigurator.cs` | **The** `IChatClientFactory` registration for grain tests (C5) — must be able to express a no-tools model |
| `tests/Agent42.Tests/FakeChatClient.cs` | A `[tools:N]` echo, in the house style |
| `tests/Agent42.Tests/*` | New: `ModelIdTests`, `ModelProviderTests`, `ModelCatalogTests`, `ModelPickerAssetsTests`; extended: `AgentGrainTests`, `AgentApiTests` |

`src/Agent42.AppHost/AppHost.cs` and `appsettings.Development.json` need **no** change (verified).

## Context & constraints

Findings the executor cannot cheaply re-derive. Each has an evidence path.

**C1 — The provider separator must be `:`, not `/`.** GitHub Models' *own* model ids are
`publisher/name` (`openai/gpt-4o-mini`), so a `/`-separated qualified id is ambiguous. Qualified ids
are **`<provider>:<model>`, split on the FIRST colon**; everything after it is the provider's own
opaque model string. A value with no colon is a **legacy** id and resolves against the default
provider — which is what keeps every existing conversation working with no state migration.

**C2 — `AgentSkillsProvider` is a tool, and it is easy to miss.** It exposes a `load_skill` function
to the model (`AgentGrain.BuildAgent`, the `AgentSkillsProviderOptions` block). So a model with
`SupportsTools: false` must drop the skills provider **as well as** the seven `AIFunctionFactory`
tools, or it still gets a function schema it cannot handle. The RAG `TextSearchProvider` (ADR 0008)
is **not** a tool — it injects context before the call — and must stay on regardless.
Evidence: `src/Agent42.Agents/AgentGrain.cs:160-212`.

**C3 — Degrade, never throw, on a missing API key.** Follow ADR 0012's `GOOGLE_MAPS_API_KEY`
precedent, not ADR 0011's `SEARXNG_SECRET` one. A provider whose `ApiKey` is blank is **skipped with
a warning** at startup and the app boots on the remaining providers; the Bicep emits neither the
secret nor the env var when the param is empty (`infra/modules/container-app.bicep:165-172, 209-211`).
A GitHub token expiring must not take the app down — Foundry is the availability path. Contrast:
`SEARXNG_SECRET` has `@minLength(16)` and *does* fail the deploy, because a crash-looping sidecar
takes the backend with it.

**C4 — Do NOT rename `AgentGrainState.DeploymentName`.** The property is `[Id(2)]` under
`[GenerateSerializer]`, but **which serializer the Cosmos grain-storage provider actually uses
(Orleans binary field-ids vs. JSON property names) is unverified**, and if it is JSON a rename
silently orphans the model on every existing conversation. The value it holds becomes a qualified id;
the property keeps its name and its `[Id(2)]`. Add a comment saying exactly this.
Evidence: `src/Agent42.Agents/AgentGrainState.cs:25-26`.

**C5 — `IChatClientFactory`'s vocabulary is now wrong, and this rename IS safe.** `DefaultDeployment`
/ `GetClient(deployment)` will be handed `github:openai/gpt-4o-mini`, which is not a deployment. The
interface is not persisted, so rename to `DefaultModelId` / `GetClient(string? modelId)` and add the
capability query. **The call sites are exactly four** (`grep -rn "IChatClientFactory" tests/ src/ tools/`):
`AgentGrain`, DevHost, and in tests `AttachmentRehydrateTests.cs:42`, `CapSizedWebPageFetcher.cs:41`,
`FakeGeocoder.cs:47`, **`TestSiloConfigurator.cs:22`**. That last one is the registration the whole
grain-test suite uses, and Step 3 must widen it (see C10).

**C6 — An unknown provider in a persisted id must fall back, not fail.** If a provider is removed
from config while a conversation is pinned to `ollama:llama3.1`, resolution falls back to the default
model with a warning. A conversation must never become unopenable because of a config edit.

**C7 — No new package tree.** `OpenAI 2.10.0` and `Microsoft.Extensions.AI.OpenAI 10.6.0` are already
present transitively, and `OpenAIClient` supports `ApiKeyCredential` + a custom
`OpenAIClientOptions.Endpoint`. Add a **direct** `PackageReference` to `OpenAI` anyway (the repo does
this for `OpenTelemetry.Instrumentation.AspNetCore` with a comment, `Agent42.Backend.csproj:21-23`) —
and per hard rule 4 resolve the version with `dotnet add package OpenAI`, never typed from memory.

**C8 — The OpenAI SDK does not use `IHttpClientFactory`, so the retry gotcha does not apply — keep it
that way.** Leave the client on its default transport. If you ever set `OpenAIClientOptions.Transport`
from an `IHttpClientFactory` client it silently inherits `AddServiceDefaults`' 3-retry pipeline and
you must call `RemoveAllResilienceHandlers()` (gotchas: "A named HttpClient silently inherits…").

**C9 — Not an SSRF sink.** The endpoint comes from **config**, never from the model — exactly like
`GoogleGeocoder`'s config-pinned host (ADR 0012). No `PrivateAddressGuard`. Do not add one, and do
not let a future change accept an endpoint from a request or a tool argument.

**C10 — `SingleChatClientFactory` must be able to express a no-tools model, or three ACs are
unwritable.** `TestSiloConfigurator.cs:22` is the only factory the grain tests get; if its
`SupportsTools` returns a constant `true`, AC7/AC8/AC9 cannot be tested at all. Give it an optional
capability predicate (e.g. `Func<string?, ModelCapabilities>`, defaulting to fully-capable) so a test
can register a no-tools / no-vision model id. DevHost's static catalog should list one too, so the
degraded path is exercisable by hand.

**C11 — This ships conversation content to a third party, and the repo has never done that before.**
The session carries document excerpts (ADR 0008), image blob SAS URLs (ADR 0004), and the user's
**street address** (ADR 0012). Selecting a GitHub model sends all of it to an endpoint outside the
Entra tenant, authenticated by a PAT, under GitHub's terms rather than Azure OpenAI's. Compare the
care the repo already takes here: the whole reason identity travels by Orleans `RequestContext` and
not `Activity.Baggage` is to keep user PII out of a header going to *Azure OpenAI* — a first-party
service. This is a larger step than that. Consequences, all load-bearing:

- The **default stays Foundry**. A third-party model is opt-in, per conversation, by an explicit pick.
- Per-model **`SupportsVision`** alongside `SupportsTools`. Without it, attaching an image to a
  text-only model both fails the turn *and* hands a SAS URL for our private `attachments` container
  to a third-party fetcher. Gate it in the grain, not only in the UI.
- The ADR must **name the egress explicitly** as an accepted consequence — not bury it. It is the
  single most important thing a future reader needs to know about this feature.

**C12 — The Foundry provider's endpoint must keep coming from `AzureOpenAI:Endpoint`.** Bicep threads
it as an env var (`infra/modules/container-app.bicep:188`), and the **embedding** client — explicitly
out of scope — reads it at `Program.cs:102`. So do **not** hardcode a second copy in
`Models:Providers:foundry:Endpoint`: leave that empty in appsettings and have the binder fall back to
`AzureOpenAI:Endpoint` for an `AzureOpenAI`-kind provider. One source of truth, no new Bicep env var,
embeddings untouched. Getting this wrong means prod silently talks to the appsettings endpoint while
the Bicep param points elsewhere.

**C13 — Scope decisions (user, this session):** production wiring is **GitHub Models only**. Ollama is
supported *by construction* — it is just another `OpenAICompatible` provider — but is configured
nowhere and is unreachable from the Container App. Model discovery is **declared in config** for
OpenAI-compatible providers; Foundry keeps its live ARM query. Tool support is a **per-model config
flag**.

**C14 — More than one Foundry account must work, and discovery is the part that does NOT come free.**
`Providers` is a dictionary, so N providers of kind `AzureOpenAI` (each its own account/endpoint) chat
correctly with no extra work — and qualified ids are what make the *same* bare model name on two
accounts unambiguous (`foundry-eu:gpt-4o` vs `foundry-swe:gpt-4o`), which is the collision C1 exists to
solve. But `AzureModelCatalog` reads its ARM coordinates from three **flat, global** keys —
`AzureOpenAI:SubscriptionId` / `:ResourceGroup` / `:AccountName` (`ModelCatalog.cs:33-35`). There is
exactly one set. A second discovering Foundry provider would therefore enumerate the **first** account's
deployments and label them as its own — silently wrong, not a failure. So `ProviderOptions` carries
optional `SubscriptionId` / `ResourceGroup` / `AccountName`, **each falling back to the corresponding
`AzureOpenAI:*` key when absent** — the same fallback rule C12 gives `Endpoint`, for the same reason
(one source of truth; today's single-account config keeps working by omitting all four). Consequently
`CompositeModelCatalog` constructs **one `AzureModelCatalog` per discovering provider**, not one shared
singleton. Note what stays account-specific regardless: **embeddings** read `AzureOpenAI:Endpoint`
(out of scope), so the primary Foundry account remains privileged for RAG however many chat providers
exist; and each additional account needs its **own** *Cognitive Services OpenAI User* grant to the
container-app identity — a manual step by construction (the OpenAI grant is an `az rest` call in
deploy.yml, not Bicep, because the account lives in a different RG; a third account in a third RG may
not be grantable by the deploy SP at all without another one-time elevation). Hard rule 5: prepare the
command, hand it to the user.

**Governing docs:** no ADR pins "Azure Foundry only", so nothing is superseded — this needs a **new
ADR 0013**. Re-read before touching: the 2 MB session cap, the named-HttpClient retry pipeline, and
the fake-client echo gotcha.

## Steps

### Step 1 — Spike: confirm GitHub Models actually works this way *(blocking; do first)*

Everything below assumes an unverified external contract. Use `/spike-dotnet` (throwaway file-based
app outside the repo tree, pinned to the repo's package versions), against a real GitHub PAT with
`models:read`. Establish:

1. The correct inference endpoint and the exact model-id string (`https://models.github.ai/inference`
   + `openai/gpt-4o-mini` is the **hypothesis, not a fact** — the older
   `https://models.inference.ai.azure.com` + bare `gpt-4o` form may be what works).
2. That `new OpenAIClient(new ApiKeyCredential(pat), new OpenAIClientOptions { Endpoint = … })`
   `.GetChatClient(id).AsIChatClient()` streams a reply.
3. **Whether function calling works** — register one trivial tool and confirm it is invoked. This
   decides whether `SupportsTools: true` is even truthful for these models.
4. **Whether a history containing tool calls is accepted when the request declares no tools** (S6 —
   this is the model-switch path, Step 6b). If it 400s, the strip is mandatory, not defensive.
5. The **context-window and rate-limit shape** (a 400 context-length body and a 429 body). Our session
   replays an 8 KB fetched page + search snippets + RAG excerpts every turn (gotcha: "An agent tool's
   RETURN value is persisted"), and the GitHub free tier caps input tokens far below Foundry — a
   context-length 400 is the *likely* first failure in real use, not the 429.

**Verify:** the spike prints a streamed reply and a tool round-trip. **Record the confirmed endpoint,
id format and limits in the ADR (Step 9)** — that is the spike's durable value. If reality differs
from the hypothesis, only Step 4's config values change.

#### Step 1 — RESULTS (spike run 2026-07-14, real PAT, pay-as-you-go + budget enabled)

Ran against `OpenAI 2.10.0` + `Microsoft.Extensions.AI.OpenAI 10.6.0` — the versions the repo already
resolves transitively, confirming C7.

1. **Endpoint + id format: hypothesis CONFIRMED.** `https://models.github.ai/inference` +
   `openai/gpt-4o-mini` streams. (The legacy `https://models.inference.ai.azure.com` + bare `gpt-4o-mini`
   *also* still works — but we use the former, which is why C1's first-colon split matters: the id
   contains a `/`.)
2. **`new OpenAIClient(new ApiKeyCredential(pat), new OpenAIClientOptions { Endpoint = … })`
   `.GetChatClient(id).AsIChatClient()` streams.** Confirmed.
3. **Function calling WORKS** — tool invoked, result used in the reply. `SupportsTools: true` is truthful
   for the OpenAI-published models. Not universal: `microsoft/phi-4` 400s on a tools request and
   `deepseek/DeepSeek-V3-0324` never calls the tool — which is exactly why tool support is a
   **per-model** flag (C13) and not a per-provider one.
4. **A tool-bearing history with NO tools declared is ACCEPTED** (it answered from the
   `FunctionResultContent`). So the Step 6b strip is **defensive, not mandatory** — implement it anyway
   (cheap; the failure it prevents is a bricked conversation, and the acceptance is a service behaviour
   we do not control).
5. **Limits: the plan guessed the wrong status code.** Not a 400 — **HTTP 413**, body
   `{"error":{"code":"tokens_limit_reached","message":"Request body too large for <model> model. Max
   size: N tokens."}}`. **Per-request input caps: `gpt-4o`/`gpt-4.1` 16,000; `gpt-4o-mini`/`gpt-4.1-mini`
   8,000; `o4-mini` 4,000.** These did **not** change when pay-as-you-go billing and a budget were
   enabled (measured before and after, twice) — treat the cap as structural until proven otherwise.
   **This is tight for *this* app**: the session replays an 8 KB fetched page + search snippets + RAG
   excerpts every turn (gotcha: "An agent tool's RETURN value is persisted"), so a web- or
   document-heavy conversation hits 413 on a GitHub model long before it would on Foundry. Anything
   that surfaces or matches this error must key on **413**, not 400/429.

**Confirmed model flags (declare these in Step 4).** Vision was verified by asking each model to name the
technology in a logo image supplied as remote `UriContent` — the shape `AgentGrain` actually sends:

| id | tools | vision | input cap |
|---|---|---|---|
| `openai/gpt-4o` | yes | yes | 16,000 |
| `openai/gpt-4.1` | yes | yes | 16,000 |
| `openai/gpt-4o-mini` | yes | yes | 8,000 |
| `openai/gpt-4.1-mini` | yes | yes | 8,000 |
| `openai/o4-mini` | yes | yes | 4,000 |

**Two traps the spike itself fell into — do not repeat them when re-verifying:**
- **A remote image URL IS accepted** (this is not an Azure-AI-Inference-style base64-only endpoint). An
  early run concluded "vision unsupported, HTTP 400" — that was **Wikimedia 403ing GitHub's image
  fetcher**, not GitHub refusing remote URLs. Retested against `raw.githubusercontent.com`: accepted, as
  `UriContent` *and* as inlined base64. **Consequence: C11's SAS-URL egress is a LIVE channel** — a
  GitHub-hosted model's backend really will fetch a SAS URL into our private `attachments` container. The
  Step 6c vision gate is load-bearing, not theoretical.
- **A "did it see the image" probe must be discriminating.** Asking "what animal is this?" and accepting
  any non-empty reply marks text-only models as vision-capable: `meta/Llama-3.3-70B-Instruct` confidently
  answered *"Google"*. Ask for something only the image reveals, and check the answer.

**Not confirmed:** the 429 body shape (never hit it), and whether the input cap lifts under billing (it
did not, within this session).

### Step 2 — Qualified model ids *(parallelizable with Step 3)*

`src/Agent42.Backend/ModelProviderOptions.cs` (new): a `ModelId` helper — `Parse(id, defaultProvider)
→ (Provider, Model)` splitting on the **first** `:`; unqualified → default provider;
`Qualify(provider, model)`. Plus the options records bound from `Models:`:
`ModelProvidersOptions { string Default, Dictionary<string, ProviderOptions> Providers }`,
`ProviderOptions { ProviderKind Kind, string? Endpoint, string? ApiKey, string? DisplayName, bool Discover, string? SubscriptionId, string? ResourceGroup, string? AccountName, List<ConfiguredModel> Models }`,
`ConfiguredModel { string Id, string? DisplayName, bool SupportsTools, bool SupportsVision }`,
`enum ProviderKind { AzureOpenAI, OpenAICompatible }`, and a `ModelCapabilities` record the factory
returns.

The four `AzureOpenAI`-kind fields (`Endpoint`, `SubscriptionId`, `ResourceGroup`, `AccountName`) each
fall back to the matching `AzureOpenAI:*` config key when absent (C12, C14). Put that fallback in **one**
resolver used by both the factory and the catalog — not two copies (`duplication_pct_max: 3`).

**Verify:** `dotnet test --filter ModelIdTests` — round-trip; first-colon split with a `/` in the model
part (C1); legacy unqualified fallback.

### Step 3 — `IChatClientFactory` gains model vocabulary + capabilities *(parallelizable with Step 2)*

`src/Agent42.Agents/IChatClientFactory.cs`: rename to `DefaultModelId` / `GetClient(string? modelId)`,
add `ModelCapabilities GetCapabilities(string? modelId)` (`SupportsTools`, `SupportsVision`).
`SingleChatClientFactory` takes an **optional capability predicate**, defaulting to fully-capable, so
a test can register a no-tools model (C10). Fix the four call sites named in C5 — including
`TestSiloConfigurator.cs:22`.

**Verify:** `dotnet build Agent42.slnx` is clean — a rename plus one member.

### Step 4 — The `Models:` config section

`src/Agent42.Backend/appsettings.json`: add `Models:` with the `foundry` provider (kind `AzureOpenAI`,
**no `Endpoint`** — it falls back to `AzureOpenAI:Endpoint`, C12 — `Discover: true`) and the `github`
provider (kind `OpenAICompatible`, endpoint + model list **as confirmed by Step 1**, `ApiKey` absent
because it is a secret, with a `"//"` comment saying so in the `Geocoding` section's house style).
`Models:Default` is `foundry:gpt-4o`.

Keep every existing `AzureOpenAI:*` key — the ARM catalog, the embedding client (C12) and the Bicep
env vars all still read them. The shipped `foundry` provider therefore declares **none** of
`Endpoint`/`SubscriptionId`/`ResourceGroup`/`AccountName` and inherits all four (C14); a *second*
Foundry account is added by declaring all four on a new provider key, with no code change and no new
Bicep env var. Say that in the section's `"//"` comment — it is the whole point of the shape.

### Step 5 — `MultiProviderChatClientFactory` + `CompositeModelCatalog`

`ChatClientFactory.cs`: replace `AzureOpenAIChatClientFactory` with `MultiProviderChatClientFactory`,
holding one underlying client per provider (`AzureOpenAIClient` + `TokenCredential` for `AzureOpenAI`;
`OpenAIClient` + `ApiKeyCredential` + `Endpoint` for `OpenAICompatible`) and caching `IChatClient`s
**keyed by the qualified id**. Every client keeps the `.UseOpenTelemetry(sourceName: OtelSourceName,
EnableSensitiveData = true)` wrapper — do not drop it for the new kind. Unknown provider → default,
with a warning (C6). `GetCapabilities` reads the config flags; a discovered Foundry model is fully
capable.

`ModelCatalog.cs`: `ModelInfo` becomes `(string Id, string Provider, string? DisplayName, string? Model,
string? Version, bool SupportsTools, bool SupportsVision)` — `Id` is the **qualified** id, replacing
`Deployment` (which app.js reads as `m.deployment`; Step 7 changes with it). `CompositeModelCatalog`
fans out over the configured providers: `AzureOpenAI` + `Discover` → **one `AzureModelCatalog` per such
provider**, each constructed with *that provider's* resolved ARM coordinates (C14 — not the single
global-config singleton it is today), with its ids qualified; `OpenAICompatible` → the configured list.
A provider that throws contributes nothing rather than failing the whole catalog — today's
`DefaultOnly()` degradation, per provider.

`AzureModelCatalog` therefore stops reading `IConfiguration` for its coordinates and takes them as
constructor arguments (subscription/RG/account, already resolved). Its existing no-coordinates warning
path stays — it just now names the provider it degraded.

`Program.cs`: bind `Models:`, skip a blank-`ApiKey` provider with a warning (C3), register both
singletons, keep the eager `GetClient(null)` startup check.

**Verify:** `dotnet test --filter "ModelProviderTests|ModelCatalogTests"`.

### Step 6 — The grain honours capabilities

**6a — tools.** `AgentGrain.BuildAgent`: when `!GetCapabilities(modelId).SupportsTools`, pass
`Tools = []` **and** omit `skillsProvider` from `AIContextProviders` (C2). The document
`TextSearchProvider` still attaches when the conversation has documents.

**6b — tool history on a downgrade.** `ChatCoreAsync:360-373` rehosts the *existing session* on the
newly picked model. Switching a conversation that has already called tools onto a no-tools model
would replay `FunctionCallContent` / `FunctionResultContent` in a request that declares no tools —
which OpenAI-compatible endpoints commonly reject. Strip tool-call and tool-result content from the
session on a capability downgrade (Step 1 item 4 says whether this is mandatory or merely defensive;
implement it either way — it is cheap and the failure is a bricked conversation).

**6c — images.** When `!SupportsVision`, do not attach image `UriContent` to the turn (C11); tell the
user plainly that the selected model cannot see images. This keeps a private-container SAS URL from
being handed to a third-party endpoint that cannot use it anyway.

`FakeChatClient` gains a `[tools:N]` echo marker, matching the existing `[sys:]`/`[ctx:]`/`[img:]`/
`[uctx:]` shape (gotcha: "Testing that a bound persona reaches the model needs the fake to echo its
instructions") — the fake proves what reached the model; do not switch to out-of-band recording.

**Verify:** `dotnet test --filter AgentGrainTests`.

### Step 7 — Picker shows providers *(four sites, not one)*

`wwwroot/app.js`:
- `fillModelOptions` (`:136`) — reads `m.id` (was `m.deployment`), groups options under an `<optgroup>`
  per provider, label prefers `m.displayName`. **It is shared by the composer picker AND the agent
  dialog** (`:129` and `:318`), so both get grouping for free — but both must be checked.
- `setModel` (`:149`) — appends a bare `<option>` when the id is unknown; with optgroups present that
  lands as their sibling. Fix it to append into an "unknown" group or to the select's end deliberately.
- `openAgentDialog` (`:322-328`) — a **second, inline copy** of that same append logic for
  `agentModelSelect`. Fold it into the shared helper rather than duplicating the fix
  (`duplication_pct_max: 3`).
- The agent list chip (`:266`, `model.textContent = agent.model`) now renders a qualified id — show the
  display name, or at least strip the provider prefix.

`AgentDefinition.Model` / `AgentBinding.Model` hold qualified ids from here on; existing stored agents
hold legacy bare ids and resolve via C1. No migration.

**Verify:** `dotnet test --filter ModelPickerAssetsTests` — a source-text asset test in the house style
of `LocationAssetsTests` / `PwaAssetsTests` (there is no JS runner in the repo). Then **exercise both
pickers by hand** in DevHost (`dotnet run --project tools/Agent42.DevHost`) — the asset tests are
heuristics and cannot prove either picker renders.

### Step 8 — Ship the GitHub token to prod

Copy the `googleMapsApiKey` pattern **exactly** (`infra/main.bicep:39-41, 194`;
`infra/modules/container-app.bicep:92-94, 165-172, 209-211`; `deploy.yml:96`): a `@secure()`
`githubModelsToken string = ''` param with **no `@minLength`**, conditionally emitting a
`github-models-token` secret and a `Models__Providers__github__ApiKey` env var — both omitted when
empty (C3). `deploy.yml` passes `githubModelsToken="${{ secrets.GITHUB_MODELS_TOKEN }}"`.

> **Manual prerequisite for the user:** create the `GITHUB_MODELS_TOKEN` repo secret — a GitHub PAT
> with the `models:read` scope. Absent, the app deploys and runs on Foundry alone and the picker shows
> no GitHub models. **Note the name:** `GITHUB_*` is a reserved prefix for GitHub-provided secrets in
> Actions — **verify the name is settable, and fall back to `GH_MODELS_TOKEN` if Actions rejects it.**
> Cheap to check now, annoying to discover at deploy time.

**Verify:** `az deployment group validate` against `rg-agent42` (a bad param shape is rejected there,
not by `az bicep build`), then deploy and confirm a GitHub model answers in prod.

### Step 9 — Docs

New **ADR 0013** (via `/decide`): the provider/model config section, `provider:model` ids, the
ApiKeyCredential path. It must contain a **Consequences section naming the third-party data egress**
(C11) — document excerpts, image SAS URLs and the user's street address can now leave the Entra tenant
on an opt-in model pick — and record the Step 1 spike's **confirmed** endpoint, id format and context
limits. Rejected alternatives: live `/v1/models` discovery; a flat non-qualified namespace; failing
hard on a missing key; routing embeddings multi-provider too. It must also record that **N Foundry
accounts are supported by config** (C14) — the per-provider ARM coordinates, their fallback to the
`AzureOpenAI:*` keys, and the two things that stay account-specific: embeddings stay on the primary
account, and every additional account needs its own manual *Cognitive Services OpenAI User* grant.

New **gotchas** (via `/gotcha`): (a) GitHub model ids contain `/`, so the separator is `:` and the split
is on the first colon (C1); (b) `AgentSkillsProvider` is a tool and must be dropped for a no-tools model
(C2); (c) `AgentGrainState.DeploymentName` keeps its name on purpose (C4); (d) the Foundry provider's
endpoint still comes from `AzureOpenAI:Endpoint` because Bicep and the embedding client read it (C12).

Update **CLAUDE.md**'s repo-specific notes with a Models/providers bullet naming the manual secret
prerequisite and the egress consequence, in the style of the ADR 0011/0012 bullets.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---------------------|------|------|
| AC1 | `provider:model` parses on the first colon; a model containing `/` survives intact | `ModelId_SplitsOnFirstColon_AndKeepsSlashesInModel` | `tests/Agent42.Tests/ModelIdTests.cs` |
| AC2 | A legacy unqualified id (`gpt-4o`) resolves against the default provider | `ModelId_Unqualified_ResolvesToDefaultProvider` | `tests/Agent42.Tests/ModelIdTests.cs` |
| AC3 | An id naming an unknown provider falls back to the default, no throw | `UnknownProvider_FallsBackToDefault` | `tests/Agent42.Tests/ModelProviderTests.cs` |
| AC4 | A provider with a blank ApiKey is skipped and the default model still resolves | `BlankApiKeyProvider_IsSkipped_DefaultStillResolves` | `tests/Agent42.Tests/ModelProviderTests.cs` |
| AC5 | Two providers yield distinct clients; each qualified id is built once and cached | `Factory_CachesOneClientPerQualifiedId` | `tests/Agent42.Tests/ModelProviderTests.cs` |
| AC6 | The catalog returns qualified ids carrying provider + capability flags | `Catalog_ReturnsQualifiedIds_WithProviderAndCapabilities` | `tests/Agent42.Tests/ModelCatalogTests.cs` |
| AC7 | A `SupportsTools: false` model registers **zero** tools **and no** `load_skill` | `NoToolModel_RegistersNoTools_AndNoSkillsProvider` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC8 | A tools-capable model still registers all seven tools (no regression) | `ToolModel_StillRegistersAllTools` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC9 | A document conversation on a no-tools model still gets RAG retrieval | `NoToolModel_StillGetsDocumentRetrieval` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC10 | Switching a conversation with tool history onto a no-tools model strips tool content and still answers | `SwitchToNoToolModel_StripsToolHistory_AndStillReplies` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC11 | A `SupportsVision: false` model receives no image `UriContent`, and the user is told why | `NoVisionModel_SendsNoImageContent` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC12 | An existing conversation persisted with a bare `gpt-4o` still opens and chats | `LegacyPersistedModel_StillResolves` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC13 | `/api/models` serves qualified ids + provider + capabilities, camelCase | `ModelsEndpoint_ReturnsQualifiedIdsWithProvider` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC14 | Both pickers read `.id`/`.provider` and render an `optgroup`; no duplicated append logic | `ModelPicker_ReadsQualifiedId_AndGroupsByProvider` | `tests/Agent42.Tests/ModelPickerAssetsTests.cs` |
| AC15 | Two `AzureOpenAI` providers each resolve their **own** ARM coordinates and endpoint; one omitting them inherits the `AzureOpenAI:*` keys | `TwoFoundryProviders_ResolveOwnCoordinates_AndInheritWhenAbsent` | `tests/Agent42.Tests/ModelProviderTests.cs` |
| AC16 | The catalog qualifies each Foundry provider's deployments under **that** provider, so the same bare model on two accounts yields two distinct ids | `TwoFoundryProviders_YieldDistinctQualifiedIds` | `tests/Agent42.Tests/ModelCatalogTests.cs` |

**Not covered offline** — state plainly, do not fake:
- That GitHub Models accepts our request shape, streams, and honours tools. The Step 1 spike proves it
  once against the real service; the suite never calls it.
- That the Container App secret lands as `Models__Providers__github__ApiKey`.
- That the app **starts** with a blank-key provider (nothing in the suite runs `Program.cs` —
  `AgentApiTests.cs:37` builds its own `WebApplication`). AC4 proves the options/factory skip; startup
  is a deploy-smoke check.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (85feb54) — never re-derived from memory:

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0` (hard block)
- `dependencies.add_policy: latest-stable` — the `OpenAI` direct reference is resolved by
  `dotnet add package`, never typed from memory (C7)
- Branch: `feature/multi-provider-models` + MR. **Never commit to `main`** (`branches.protected`).
- Local enforcement `soft_warning`; CI hard-blocks build, tests, `dotnet format`, vulnerable packages,
  doc-path integrity.

## Out of scope

- **Embeddings stay on Azure Foundry.** `text-embedding-3-large` is not multi-provider; document RAG
  (ADR 0007) is untouched, and `AzureOpenAI:Endpoint` remains its source (C12).
- **Ollama is not configured anywhere**, in prod or DevHost — it works by config if someone points a
  provider at it, and that is the whole deliverable. DevHost keeps its canned client.
- **No live `/v1/models` discovery** for OpenAI-compatible providers (user decision, C13).
- **No per-user or per-agent API keys** — one key per provider, app-wide.
- **No second Foundry account is *configured*.** N of them work by config (C14) and the tests pin that,
  but prod ships exactly one, and its RBAC grant for any future account is manual (hard rule 5).
- **No cost or rate-limit UI.** A GitHub Models 429 or context-length 400 surfaces as a normal chat
  error (its shape is characterised by the Step 1 spike, so the message can at least be honest).
- **No per-provider egress policy** — e.g. blocking document-bearing or location-bearing conversations
  from third-party models. C11 accepts the egress; a policy layer is a separate decision.
- **No renaming of the persisted `DeploymentName` state property** (C4).

## Divergences from the plan (recorded at execution, 2026-07-14)

The plan survived contact largely intact. Where the implementation differs, and why:

1. **The 413, not a 400 (Step 1, "Out of scope").** The plan expected a context-length **400** and a
   **429**. Reality is **HTTP 413 `tokens_limit_reached`**, with per-request input caps of 16k/8k/4k that
   **billing did not lift**. Nothing in the code keys on the status code, so this changed no design — but
   it is recorded in the ADR and gotchas, because handling keyed on 400/429 would miss it entirely.

2. **Step 6b's strip is defensive, not mandatory.** The spike established that a tool-bearing history IS
   accepted when the request declares no tools. Implemented anyway, as the plan directed.

3. **The vision gate had to cover HISTORY too — the plan only specified the new turn.** Step 6c said
   "do not attach image `UriContent` to the turn". That is only half the hole: prior turns' image parts
   live on in the session and are replayed every turn, so a downgraded conversation would still ship
   every earlier SAS URL to the third party. The gate therefore moved into
   `RehydrateAttachmentUrlsAsync(bool includeImages)`, and is **reversible** (it reads `TurnAttachments`
   rather than deleting parts, so switching back to a vision model restores them). This also forced a new
   `[allimg:N]` fake-client marker: the planned `[img:N]` counts only the last user message and is
   therefore blind to exactly this bug. New gotcha written.

4. **`[tools:N]` became `[tools:a,b,c]`.** A count would have hard-coded how many tools
   `AgentSkillsProvider` happens to ship (the capable path turns out to be **10**, not the 8 assumed).
   Names make the load-bearing half of AC7 — "and no `load_skill`" — directly assertable.

5. **The user-facing "model can't see images" notice is a stream event, not a message part.** The plan
   said "tell the user plainly". The first attempt appended a `TextContent` note to the user's turn —
   which `GetHistoryAsync` concatenates into that turn's text, so it would have rendered **inside the
   user's own chat bubble** on every reload. It is now an `AgentStreamEvent` `TextDelta`, client-only and
   never persisted.

6. **New file `ModelProviderBuilder.cs` (not in the plan's affected-areas table).** The provider-building
   rules (which providers are skipped, what a Foundry provider inherits, what a declared model
   contributes) were going to live inline in `Program.cs` — where **nothing in the test suite runs them**,
   which would have made AC4 and AC15 untestable. Extracted so they are.

7. **A fifth frontend site.** Step 7 named four; `lockPicker` (`app.js`) also rendered a raw model id into
   the locked-picker label. Fixed with the same `modelLabel()` helper.

8. **Secret name is `GH_MODELS_TOKEN`, not `GITHUB_MODELS_TOKEN`.** As Step 8 warned to check: `GITHUB_*`
   is a reserved prefix for GitHub-provided Actions secrets. Bicep param stays `githubModelsToken`.

9. **`OpenAI` resolved to 2.12.0**, not the 2.10.0 already present transitively — `dotnet add package`
   per hard rule 4 / `add_policy: latest-stable`. Build and suite are green on it.

10. **AC13's endpoint test needed the test host's catalog to have models in it** — it was registered with
    an empty `StaticModelCatalog`, so any assertion on the payload would have been vacuous.

### Found by the `critic` pass on the diff (all fixed before the MR)

11. **An image-only turn on a no-vision model was left with ZERO content parts** — the composer allows a
    blank-text turn with a photo, so stripping its images emptied the message, and an empty message is
    rejected outright by OpenAI-compatible endpoints. That is *the* bricked conversation this feature
    exists to prevent, introduced by the fix for it. Stripped turns now keep a placeholder `TextContent`.
    A `FakeChatClient` cannot reject an empty message the way the real service does, so the obvious test
    would have passed vacuously — hence the new `[emptymsg:N]` echo. **Mutation-checked**: remove the
    placeholder and `NoVisionModel_ImageOnlyTurn_StillReplies` fails.

12. **`Models:Default` had made `AzureOpenAI__DeploymentName` dead config.** Hardcoding
    `"Default": "foundry:gpt-4o"` in appsettings silently overrode the env var Bicep threads in — the
    exact "looks authoritative, changes nothing" trap the SearXNG `server.port` gotcha describes. The
    default is now **derived** from `AzureOpenAI:DeploymentName`, the same inheritance rule C12 gives
    `Endpoint`. New gotcha bullet.

13. **`BindAgentAsync` was the second, unguarded model-change path.** An agent's `Model` is a qualified id
    now, so an agent can be bound to a no-tools model; the tool-history strip only ran on the picker path.
    Binding happens on an empty session *today*, but that is an assumption about a caller, not an
    invariant. It now strips too, and `BindingToNoToolModel_RegistersNoTools` covers it.

14. **The history vision-strip was keyed off `TurnAttachments`**, which deserializes empty for
    conversations predating it — so those would have kept leaking image parts. It now strips the
    message's own parts regardless.

15. **C4 was quietly skipped**, and the gotcha I wrote cited the missing comment as evidence. The
    `AgentGrainState.DeploymentName` comment now exists (and `IAgentGrain`'s stale "deployment name"
    vocabulary is fixed). A fabricated evidence path in a knowledge file is the failure this plan's own
    review called the worst kind of error — and I made it anyway.

16. **`AzureModelCatalog`'s ARM query is now tested offline** via a canned `HttpPipelineTransport`
    (`CannedArmTransport`), rather than being written off as prod-only. It pins the thing multi-account
    Foundry actually depends on: each deployment qualified under **its own** provider key, and each
    provider querying **its own** account. This took new-code coverage from 78.8% to 90.4%.

## Review

A `critic` subagent reviewed this plan against the repo's knowledge files and the code at `85feb54`.
Findings and disposition:

| # | Finding | Disposition |
|---|---------|-------------|
| S1 | AC7/8/9 unwritable: `SingleChatClientFactory` reports `SupportsTools: true` unconditionally, and it is the only factory the grain tests get | **Accepted** — C10 + Step 3 give it a capability predicate; `TestSiloConfigurator.cs` added to affected areas |
| S2 | C5's caller list named `TestServiceRegistration.cs`, which has no `IChatClientFactory` reference; the real site is `TestSiloConfigurator.cs:22` | **Accepted** — verified by grep and corrected. (A fabricated evidence path in a cold handoff is the worst kind of error; it was mine.) |
| S3 | A new third-party data-egress path (doc excerpts, image SAS URLs, the user's street address) with none of the analysis the repo applies to every other outbound channel; and no vision flag, so an image attach on a text-only model fails the turn *and* leaks a SAS URL | **Accepted** — new C11; `SupportsVision` added to the options, Step 6c, AC11; the ADR must name the egress in Consequences |
| S4 | `AzureOpenAI:Endpoint` would become two sources of truth — Step 4 dropped it from the "keep" list, but Bicep threads it (`container-app.bicep:188`) and the out-of-scope embedding client reads it (`Program.cs:102`) | **Accepted** — new C12: the Foundry provider's endpoint falls back to `AzureOpenAI:Endpoint`; no second copy, no new Bicep env var |
| S5 | Step 7 understated the frontend: `fillModelOptions` is shared with the agent dialog, `setModel` and `openAgentDialog` each append options by hand, and the agent chip renders a raw model id | **Accepted** — verified by grep; Step 7 now names all four sites and folds the duplicated append logic |
| S6 | Switching a conversation with tool-call history onto a no-tools model replays tool content in a request declaring no tools — commonly a 400 | **Accepted** — Step 6b (strip on downgrade), AC10, and Step 1 item 4 to establish whether it is mandatory |
| S7 | The spike should establish the context window, not just the 429 — a context-length 400 is the likelier first real failure given the session replays tool returns every turn | **Accepted** — Step 1 item 5 |
| S8 | Step 6's "record the ChatOptions" deviates from `FakeChatClient`'s echo-marker shape, which is itself a gotcha | **Accepted** — Step 6 now specifies a `[tools:N]` echo marker |
| S9 | AC4's "app still starts" is untestable where mapped — nothing in the suite runs `Program.cs` | **Accepted** — AC4 narrowed to the skip behaviour; startup moved to "Not covered offline" |

No findings rejected.
