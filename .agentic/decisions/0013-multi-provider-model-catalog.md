---
id: 0013
date: 2026-07-14
status: accepted
tags: [models, providers, github-models, openai-compatible, config, security, data-egress, capabilities]
---

# A provider/model catalog: any OpenAI-compatible endpoint, named by a qualified `provider:model` id

> **Amended by [ADR 0017](0017-foundry-provider-api-key-auth.md) (2026-07-16) — credential mechanism
> only.** The `AzureOpenAI` provider kind is no longer reached with the managed identity: it now
> authenticates with an Azure OpenAI **API key** (fatal if blank — Foundry is the availability path),
> and ARM model **discovery** is dropped (a data-plane key can't authenticate ARM), so a key-based
> Foundry provider **declares** its deployments instead. Everything else here stays authoritative: the
> qualified-id split, capability flags, and the cross-tenant egress position. `status: accepted` on
> purpose — see 0017's supersession note.

## Context
The app could only talk to one place. `AzureOpenAIChatClientFactory` was hardwired to a single Azure AI
Foundry endpoint with `DefaultAzureCredential`, and a model was named by a bare deployment string
(`gpt-4o`). We wanted GitHub Models available in production, and an Ollama/vLLM endpoint reachable by
config alone for anyone who points at one.

Two endpoints, reached by **two different credential types** (`TokenCredential` vs `ApiKeyCredential`),
with model names that are **no longer unique across them**, is exactly the shape a config section exists
to express. Leaving Foundry as a privileged special case would have meant two ways to name a model and a
factory that branched on which one it got.

**This routes conversation content to a third party.** That is not a side effect of the feature — it *is*
the feature, and it is why this ADR carries more weight than its diff suggests. See Consequences.

## Decision
A `Models:` config section with a **provider map**. Each provider is either `AzureOpenAI` (reached with
the container app's managed identity) or `OpenAICompatible` (reached with an API key against a configured
endpoint). `MultiProviderChatClientFactory` holds one SDK client per provider and caches an `IChatClient`
per **qualified model id**; `CompositeModelCatalog` fans out over the providers to build the picker.

A model is named `provider:model`, **split on the first colon**. An id with no colon is a **legacy** id
and resolves against the default provider — which is why this shipped with **no state migration** and no
rename of the persisted `AgentGrainState.DeploymentName` property.

Per-model **capability flags** (`SupportsTools`, `SupportsVision`) are declared in config and enforced in
the **grain**, not the UI.

## What the spike established (2026-07-14, real PAT)
The external contract was unverified, so it was measured before anything was built. Recorded here because
it is the durable value of that spike. It ran against `OpenAI 2.10.0` — the version already in the tree
transitively at the time; the direct reference the app now carries resolved to **2.12.0** (`dotnet add
package`, latest-stable). The endpoint contract below is the *service's*, not the SDK's, and the suite is
green on 2.12.0 — but if you are debugging a client-side surprise, that version gap is the first thing to
rule out.

- **Endpoint + id format:** `https://models.github.ai/inference` with ids like `openai/gpt-4o-mini`. The
  older `https://models.inference.ai.azure.com` + bare `gpt-4o-mini` form also still answers.
  **GitHub's ids contain a `/`** — which is precisely why the qualified-id separator is `:` and not `/`.
- **`new OpenAIClient(new ApiKeyCredential(pat), new OpenAIClientOptions { Endpoint = … })` streams.**
- **Function calling works** — but **not for every model**: `microsoft/phi-4` rejects a tools request
  outright (HTTP 400) and `deepseek/DeepSeek-V3-0324` simply never calls the tool. Hence a **per-model**
  flag, not a per-provider one.
- **Vision works via a remote image URL** — the endpoint fetches it server-side. It is *not* a
  base64-only endpoint.
- **Per-request input caps are small, and the status code is not the one you would guess:** **HTTP 413**,
  `{"error":{"code":"tokens_limit_reached", …"Max size: N tokens."}}`. **16,000** tokens for
  `openai/gpt-4o` and `openai/gpt-4.1`; **8,000** for the `-mini` models; **4,000** for `openai/o4-mini`.
  Enabling pay-as-you-go billing **and** a budget did **not** lift them (measured before and after).
- **A tool-bearing history is ACCEPTED when the request declares no tools** — so the downgrade strip
  (below) is defensive, not mandatory. It is implemented anyway: it is cheap, the behaviour is the
  service's to change, and other OpenAI-compatible endpoints commonly 400 on it.

## Consequences

### This ships conversation content to a third party, and the repo has never done that before
A conversation carries document excerpts (ADR 0008), image SAS URLs (ADR 0004), and the user's **street
address** (ADR 0012). Selecting a GitHub model sends all of it to an endpoint **outside the Entra
tenant**, authenticated by a PAT, under GitHub's terms rather than Azure OpenAI's. For scale: the reason
identity travels by Orleans `RequestContext` and not `Activity.Baggage` (ADR 0010) is to keep user PII
out of a header going to *Azure OpenAI* — a **first-party** service. This is a much larger step than
that, taken deliberately. Three things follow, and all are load-bearing:

- **The default stays Foundry.** A third-party model is opt-in, per conversation, by an explicit pick.
- **Images are gated on `SupportsVision` in the grain.** The spike confirmed a vision-capable model's
  host really does fetch the URL it is given — so sending an image SAS URL to a model that cannot read
  images would hand a live, credential-bearing URL for our **private** `attachments` container to a third
  party for nothing at all. The gate covers **history as well as the new turn**: prior turns' image parts
  live on in the session and would otherwise be replayed. The withholding is **reversible** —
  `TurnAttachments` is the durable record, so switching back to a vision model restores them.
- **The `fetch_web_page` exfiltration channel (ADR 0011) now drains to a third party too.** The
  `websearch` skill only *mitigates* it. This ADR accepts that; a per-provider egress policy (e.g.
  blocking document- or location-bearing conversations from third-party models) is a separate decision.

Two accepted limitations of the image gate, so nobody rediscovers them as bugs: the "this model cannot
see images" notice is **streamed to the client and never persisted**, so on reload the image sits in the
user's bubble with a reply that no longer explains it was ignored; and a turn whose images are withheld
keeps a **placeholder text part** in the model's view (an image-only turn would otherwise be left with
zero content parts, and an empty message is rejected outright — the bricked conversation this feature is
supposed to prevent).

### Small context windows will bite this app specifically
16k input tokens is tight *here*, because the session replays an 8 KB fetched page, search snippets and
RAG excerpts on **every turn** (see the "a tool's RETURN value is persisted" gotcha). A web- or
document-heavy conversation reaches **413** on a GitHub model long before it would on Foundry. There is
no cost or rate-limit UI: it surfaces as a normal chat error.

### A missing key degrades; it does not fail the deploy
A provider with a blank `ApiKey` is **skipped with a warning** and the app boots on the rest. This follows
ADR 0012's `GOOGLE_MAPS_API_KEY` precedent and deliberately **not** ADR 0011's `SEARXNG_SECRET` one (which
*does* fail the deploy, because a crash-looping sidecar takes the backend down with it). An expiring
GitHub PAT must never take the app down — Foundry is the availability path.

### N Foundry accounts work by config
`Providers` is a map, so several `AzureOpenAI` providers can coexist, and qualified ids are what make the
same deployment name on two accounts unambiguous. The four Foundry fields (`Endpoint`, `SubscriptionId`,
`ResourceGroup`, `AccountName`) **inherit from the `AzureOpenAI:*` keys when not declared** — one source
of truth, shared with Bicep's env vars and the (out-of-scope) embedding client. Two things do *not* come
free: **embeddings stay on the primary account**, and each additional account needs its own manual
*Cognitive Services OpenAI User* grant.

### Not an SSRF sink — keep it that way
The endpoint comes from **config**, never from the model or a request. That is what keeps this off the
path `fetch_web_page` is on, and it is the reason there is no `PrivateAddressGuard` here. Do not let a
future change accept an endpoint from a tool argument or a request body.

## Alternatives rejected
- **Live `/v1/models` discovery for OpenAI-compatible providers.** Rejected: it cannot report tool or
  vision support, which is the whole reason the catalog exists — and those flags had to be *measured*
  anyway (phi-4, DeepSeek). Foundry keeps its live ARM query, where deployments genuinely do change.
- **A flat, non-qualified model namespace.** Rejected: GitHub's own ids collide with nothing today, but
  the same deployment name on two Foundry accounts collides immediately, and a bare name gives no way to
  say which endpoint to dial.
- **`/` as the separator.** Rejected outright: GitHub's ids *contain* one (`openai/gpt-4o-mini`), so the
  split would be ambiguous. First-colon it is.
- **Failing startup on a missing API key.** Rejected — see "a missing key degrades" above.
- **Routing embeddings multi-provider too.** Out of scope: document RAG (ADR 0007) stays on Foundry, and
  `AzureOpenAI:Endpoint` remains its source.
- **Renaming the persisted `AgentGrainState.DeploymentName`.** Rejected: which serializer the Cosmos
  grain-storage provider actually uses (Orleans binary field-ids vs. JSON property names) is unverified,
  and if it is JSON a rename would silently orphan the model on every existing conversation. The property
  keeps its name and its `[Id(2)]`; only the *value* it holds becomes a qualified id.
