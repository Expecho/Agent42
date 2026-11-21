# Plan: Auto routes image requests to the user's image-generation model

planned-against: main @ db018dc (PR #258, ADR 0137, is merged; the plan was written stacked on it and re-pointed here on 2026-11-21)
approved: 2026-11-21 (by the user, in chat, after the critic pass recorded in Review)

## Overview

Today the Auto router (ADR 0137) only chooses among chat models: `FilterCandidates` drops every non-chat model, so "draw a cat"
under Auto is answered in text, and a conversation that was left on an image model is moved to a chat model by its first Auto turn.
Add the user's image-generation models as Auto options. When the message asks to create or edit a picture, run THAT TURN on the image
model, without moving the conversation's ongoing chat model. Why now: Auto was meant to remove manual model picking, and images are
the one case it still cannot cover.

The executor branches `feature/auto-image-routing` from `main`. The next free ADR number was **0138** on 2026-11-21 (0137 is the router's);
re-check `origin/main` before merging, since `AdrNumberingTests` fails on a duplicate.

## User decisions (interview, 2026-11-21)

- **Opt-in:** automatic. Every declared image-generation model is an Auto option. No per-model flag (consistent with ADR 0137's
  rejected per-model Auto opt-out; a provider the user does not want used must not be connected).
- **Edit input:** an Auto image turn edits THIS turn's attached image, else the image produced by the IMMEDIATELY preceding turn, else
  none (text-to-image). It never reuses an older image from the conversation, so "draw a dog" cannot silently become an edit of an
  earlier photo, and old photos are not sent to the image provider unprompted. Picking the image model BY HAND keeps today's
  "most recent image anywhere" rule (ADR 0020), unchanged.
- **Follow-ups:** tell the router when the previous reply was an image (one boolean line in its input) so "make it bluer" can route back.
- **Threshold:** an image option needs a higher probability than a chat option (default 0.8 vs 0.6), because a wrong image costs money
  and a wrong chat reply does not. Constants in `ModelRouterOptions`, tuned in the live check.

## Affected areas (verified against the tree on 2026-11-21; executor re-verifies paths)

Router core (Agents):
- `src/Agent42.Agents/Models/Routing/ModelRouting.cs` (`RouterCandidate`, `RouterTurn`, `RouterDecision`, `ModelRouterOptions`)
- `src/Agent42.Agents/Models/Routing/ModelRouterService.cs` (`FilterCandidates`, `Describe`, `Decide`, `ToDecision`)

Router client and catalog bridge (Backend):
- `src/Agent42.Backend/Models/Routing/FoundryDecisionClient.cs` (`BuildBody`, the `instructions` text)
- `src/Agent42.Backend/Models/Routing/UserModelRouterProvider.cs` (`CatalogModelRouter.ToCandidate`, its `IsChat` expression)

Grain:
- `src/Agent42.Agents/Conversations/AgentGrain.ModelRouting.cs` (`ResolveTurnModelAsync`, `TurnModel`, `HasImages`)
- `src/Agent42.Agents/Conversations/AgentGrain.cs` (`ChatTurnAsync`: the `SupportsImageGeneration` branch at ~:626, `SwitchModelAsync`)
- `src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs` (`GenerateImageTurnAsync` reads `_modelId`; `FindMostRecentImageRef`; `AssistantImageRef`)

Unchanged but relied on: `src/Agent42.Backend/ChatGateway/ChatReplyStreamer.cs:45` already delivers an `Image` event to chat apps;
`wwwroot/js/send.js` already renders `image` and `routed` events; `AgentStreamEvent` needs no new field (`Routed` carries id + name).

## Context and constraints the executor cannot cheaply re-derive

- **An image turn is not a chat turn.** Picking an image model today SWITCHES the conversation to it (`SwitchModelAsync` persists
  `DeploymentName`, rebuilds `_agent`), and `ChatTurnAsync` then branches to `GenerateImageTurnAsync` on `GetCapabilities(_modelId)`.
  Under Auto that would flip the ongoing chat model, and the next text message would hit the image endpoint. So an Auto image turn must
  run with a PER-TURN model id and make no switch: `_modelId`, `_agent`, `_state.State.DeploymentName` and `AutoModel` stay as they were.
  `GenerateImageTurnAsync` therefore needs the image model id as a parameter instead of reading `_modelId` (it calls
  `GetImageGenerationService(_modelId)` only). `RecordImageTurnAsync` uses `_agent` solely to serialize/deserialize the session, so a
  chat-bound `_agent` is fine (gotchas.md "image generation": the adapter dance, id-keyed `assistantImageId`).
- **History after an Auto image turn on a chat model** is already handled: a later chat turn on a no-vision model strips the assistant
  image by id and restores it (`NoVisionModel_WithholdsAssistantImage_AndRestores_AcrossToolTurn` in `AgentGrainTests.cs`). Do not
  touch that mechanism.
- **"Current model" must never be an image model, and an unrouted Auto turn must not land on the image endpoint.** If `_modelId` is an
  image model (a conversation the user left there, then chose Auto), the grain passes `CurrentModelId = null` to the router; that is how
  "none" is encoded, so the chat winner comes back `Changed = true` and switches the conversation (today's behaviour). The merged image
  condition would otherwise send ANY Auto turn that did not route (router failure or null decision, 0 or 1 eligible candidate, no
  router, incognito, agent chat) to the image endpoint, because `_modelId` is still the image model. So: an Auto request on a
  conversation whose `_modelId` is an image model, with no image decision, runs on the user's default chat model
  (`_chatClientFactory.DefaultModelId`, via `SwitchModelAsync`). The only exception is a turn skipped for another run in flight, which
  cannot switch (ADR 0106) and behaves as a by-hand image turn. A by-hand (non-`auto`) request is untouched.
- **Candidate filter.** `RouterCandidate` gains a trailing `IsImageGeneration` (default false). Image candidates are exempt from the
  tools filter (image models declare no tools) and from the vision filter (an image model CONSUMES images, ADR 0020 gotcha: the image
  branch runs before the vision gate). Chat candidates keep both filters. `HasImages` counts only USER attachments (`TurnAttachments`,
  `AgentGrain.ModelRouting.cs`): a generated image lives in `GeneratedImages` and does not set it, so after an Auto text-to-image turn a
  later chat turn still offers every chat model, and the existing strip-by-id mechanism hides the generated image from a no-vision
  model. Embedding, speech and transcription models stay excluded. `CopilotOnly` stays excluded. An image model whose provider has no
  image service (`Provider.ImageService` is null: only OpenAI-compatible providers build one, `ChatClientFactory.cs:~144`) is NOT a
  candidate: `GetForUserAsync` passes `CatalogModelRouter` a predicate built from the resolved providers, so the router can never pick a
  model that would only answer "Image generation is unavailable".
- **An image candidate is never chosen without a real classifier call that clears the bar.** `ModelRouterService` returns early on one
  eligible candidate (no call, no threshold). That shortcut now applies to a lone CHAT candidate only. If the only survivors are image
  candidates (for example an attached image and no vision chat model, or tools on and no tools chat model), return null: no routing,
  and the grain's unrouted rule above applies. One chat plus one or more image candidates still makes the call (2+ options).
- **Higher bar, and the exact `Decide` rules.** Add `ImageMinProbability` (0.8) to `ModelRouterOptions`. Let `bestImage` be the highest
  image candidate, `u` the "cannot tell" probability. The image wins iff `bestImage.p >= ImageMinProbability` AND `bestImage.p` is the
  largest value in the answer (chat candidates and `u` included); at 0.8 the second clause is implied, and a test pins it so lowering
  the constant below 0.5 cannot silently break it. Otherwise the decision is ADR 0137's `Decide` run over the CHAT candidates only
  (image keys excluded from the argmax): current chat eligible means keep it on "cannot tell", on being best, or on a weak or narrow
  win, and switch on a clear win (`MinProbability` and `Margin`); current not an eligible chat model (an image model, or filtered out)
  means the best chat candidate with no margin and no "cannot tell" check, as today; zero chat candidates means null. The "cannot tell"
  option therefore beats an image winner below the bar by falling through to the chat rule. Do not claim the numbers are tuned: the live
  check sets them.
- **Router instructions.** `FoundryDecisionClient.BuildBody` says "Pick the assistant model best suited ... cheapest model". Extend it so
  an image generator is chosen ONLY when the user asks to create, draw, generate or edit a picture (not to describe or read one: that
  is a vision chat model). Image candidates get a derived description (layer 3) such as "image generator: creates or edits a picture
  from a prompt"; the user's hint and the model card still win over it (ADR 0137 layer order). Positional keys stay (never model ids).
- **Previous-reply-was-image flag.** `RouterTurn` gains `PreviousReplyWasImage`. The router prepends one fixed line to the classifier
  `state` (`ModelRouterService`, never user text). The grain computes it from the KEY, not the ref: the last session message is an
  assistant message with an `AssistantImageId` (`AgentGrain.ImageTurn.cs`, `AssistantImageId`). The key survives the no-vision strip
  and fork, and the last message is never pruned by retention; the ref lookup in `GeneratedImages` can miss after a prune, which would
  give flag = true but no input, so the edit resolver also reads the key first and treats a missing ref as "no input". It adds a
  boolean to what ADR 0137 already sends; no content.
- **Edit input for Auto.** Add a second input resolver used only by an Auto image turn: this turn's image attachment, else the
  generated image of the last assistant message if that message is an image turn, else none. The by-hand path keeps
  `FindMostRecentImageRef`. Encode the mode as a parameter of `GenerateImageTurnAsync`, not a grain field (ADR 0106). Two consequences
  to document, not fix: a user photo followed by "make it a cartoon" in a LATER message has no input (the previous turn was chat; attach
  the photo again), and a regenerated Auto image turn (`replaceLastTurn`, ADR 0092) removes the last turn first, so it edits the image
  before it where the original may have been text-to-image.
- **Decision shape.** `RouterDecision` gains `ImageGeneration` (bool). `TurnModel` (grain) gains `ImageModel` (string?). When set,
  `ChatTurnAsync` takes the image branch with that id and skips `SwitchModelAsync`. Put the whole decision in a private helper
  (`string? ImageModelForTurn(TurnModel routing)` returning `routing.ImageModel ?? (caps(_modelId).SupportsImageGeneration ? _modelId :
  null)`), so `ChatTurnAsync` keeps ONE `if (imageModel is not null)` exactly where it has one today and its cognitive complexity does not
  grow. Do not inline the `??` and ternary: S3776 counts the ternary, `ChatTurnAsync` sits at the limit of 15, and it failed at 16 during
  ADR 0137 (the fix was extracting `EnsureModelBoundAsync` in PR #258). The executor builds before and after to confirm.
- **Reload the grain's providers before an image decision, like a chat switch does.** PR #258 added `RefreshChatClientFactoryAsync` in
  `AgentGrain.ModelRouting.cs`: a grain resolves its `IChatClientFactory` once per activation (ADR 0018), so a model added since is unknown to
  it. For an image decision the symptom would be `GetImageGenerationService(id)` returning null or a fallback, so the turn answers "Image
  generation is unavailable" for a model the router just offered. Call the same helper whenever the chosen image model differs from the one
  the conversation is on (a `Changed` image decision), after the post-await `_runsInFlight` re-check, and not when the decision is kept.
- **Routed label and flag.** An Auto image turn ALWAYS emits `Routed` (id + name of the image model), unlike a sticky chat turn: it is a
  one-off, and the label tells the user why they got a picture. `AutoModel` stays true; the picker stays on Auto.
- **Skips, unchanged from ADR 0137:** incognito (ephemeral) turns, agent-pinned chats, and a turn while another run is in flight do not
  route, so image requests there stay with the user's own pick. Say so in the docs. The post-await `_runsInFlight` re-check also applies
  to an image decision (it drops it).
- **Failure.** If routing picks the image model and generation then FAILS, the exception propagates out of the stream exactly as an
  explicit image turn's does (`GenerateImageTurnAsync` has no try/catch), and nothing is recorded in the session. No automatic fallback
  to a chat reply (it would double-spend and hide the failure). A router failure keeps the current CHAT model, or, when `_modelId` is an
  image model, falls back to the default chat model (see above).
- **Egress (CLAUDE.md rule 13, ADR 0013/0020):** an Auto image turn sends the prompt, and for an edit the input image's BYTES, to the
  image model's provider, which the user did not pick for this chat. The worst case is a misrouted turn that carries an attachment,
  which is why the bar is higher and a lone image candidate is never chosen. The new ADR records this as an extension of ADR 0137's
  egress, and states that the edit input is limited to the attached or immediately preceding image.
- **Cost and usage:** image generation is not in the usage dashboard today (not an `IChatClient`), so Auto image turns add no row.
  State it; do not invent a price (CLAUDE.md rule 3).
- **Governing ADRs and gotchas:** ADR 0137 (extend, do not supersede), 0020 (image capability, ADR amends it), 0013, 0106 (no per-run
  grain field), 0105 (port registration, unchanged), 0021 (null tools == all). Gotchas: "`auto` is a wire value", "A DecisionModel
  provider is not a chat model", "The Decision-1 router is not an `IChatClient`", and the image-generation gotcha near
  `.agentic/gotchas.md` (~858-880). Next ADR number: 0138 (re-check on `origin/main`).

## Steps

1. **ADR 0138 via `/decide`** (front matter `extends: [0020]` and `amends: [0137]`, because it changes ADR 0137's "non-chat candidates are
   excluded" line; records: image models join Auto, per-turn model id with no switch, current model
   never an image model, higher image bar, Auto edit-input rule, previous-reply-was-image flag, always-`Routed`, no fallback on image
   failure, egress extension; rejected alternatives: per-model Auto flag, reuse of "most recent image anywhere", a chat-model image
   tool, one shared threshold). Verify: `AdrNumberingTests` and `QualityGateTests` green.
2. **Router core (Agents).** `RouterCandidate.IsImageGeneration`, `RouterTurn.PreviousReplyWasImage`, `RouterDecision.ImageGeneration`,
   `ModelRouterOptions.ImageMinProbability`. `FilterCandidates` keeps image candidates (exempt from tools and vision filters, still
   excludes copilot-only and the other non-chat kinds). `Decide`: current-is-never-image, the image bar with fallback to the best chat
   candidate, and the context line in `state`. The lone-candidate shortcut applies to a chat candidate only; image-only survivors
   return null. Implement the `Decide` rules exactly as written under "Higher bar". Keep methods under the complexity limit (split
   `Decide` into a chat part and an image part). Files: `ModelRouting.cs`, `ModelRouterService.cs` (including `Describe`, which is
   Agents code). Verify: `dotnet test --filter ModelRouterServiceTests`.
3. **Backend bridge and prompt.** `CatalogModelRouter.ToCandidate` sets `IsImageGeneration` from `ModelInfo.SupportsImageGeneration`
   and `IsChat` becomes "not embeddings, speech or transcription" for the image flag; `GetForUserAsync` passes a "can generate"
   predicate from the resolved providers (`ImageService` non-null) and an image model failing it is dropped; extend the `instructions`
   in `FoundryDecisionClient.BuildBody`. Files: `UserModelRouterProvider.cs`, `FoundryDecisionClient.cs`. Verify: `dotnet test --filter "DecisionModelProviderTests|ModelCardSourceTests"`.
   Parallelizable with step 2 once the record shapes in step 2 are agreed.
4. **Grain: per-turn image model, no switch.** (Also call `RefreshChatClientFactoryAsync` before an image decision, see the constraint above.) Parameterize `GenerateImageTurnAsync` (image model id, input mode); add the
   previous-turn-image resolver; `TurnModel.ImageModel`; `ImageModelForTurn` and the single image branch in `ChatTurnAsync`; compute and
   pass `PreviousReplyWasImage` and `CurrentModelId = null` for an image `_modelId`; the unrouted-Auto rule (default chat model when
   `_modelId` is an image model and no image decision); always emit `Routed` for an image decision. On an image decision `_modelId`,
   `_agent`, `DeploymentName` and `AutoModel` are untouched. Test support: give `ProbeImageGenerator` an instance `Failure` property
   (a singleton in each silo, reset by `SharedClusterFixture.ResetSiloState`), and give `FakeModelRouter` an optional gate task so a test
   can start a second run while the router call is awaited.
   Files: `AgentGrain.ModelRouting.cs`, `AgentGrain.cs`, `AgentGrain.ImageTurn.cs`. Verify: `dotnet test --filter "ModelRouterGrainTests|AgentGrainTests"`
   (the by-hand image tests must still pass unchanged).
5. **Gateway and frontend check** (a labelling note: `ChatReplyStreamer` already handles `Image`). Confirm a routed image turn reaches a chat app and the web UI with no code change (they already
   handle `Image` and `Routed`); add a gateway test. If a gap appears, fix it here. No `sw.js` bump is expected: bump `CACHE` ONLY if a
   file under `wwwroot/js/` changes (CLAUDE.md rule 3).
6. **Docs.** `/sync-docs`: CLAUDE.md feature-map row gains 0138; update the ADR 0137 gotcha paragraph ("image models are excluded") and
   add one gotcha for "an Auto image turn never switches the conversation's model"; `docs/features/model-picker.md` (Auto section: it can
   now make pictures, edits only an attached image or the previous turn's picture, and does not route in incognito or agent chats) and
   `docs/features/bring-your-own-models.md` (image models join Auto; egress sentence).
7. **Full validation, then the live check** (post-merge, needs the user's Foundry deployment and an image model): a draw request, an
   edit follow-up, a "describe this photo" request (must NOT route to the image model), and an image-model-left conversation then Auto.
   Tune the two thresholds from real probabilities and record the numbers in the PR, not in code comments.

Parallelizable: steps 2 and 3. Step 4 after 2 and 3. Step 5 after 4. Step 6 anywhere after step 1.

## Acceptance criteria to test mapping

| AC | Test | File |
|----|------|------|
| Image candidates survive the filter, exempt from tools and vision; copilot-only, embeddings, speech and transcription stay out | `Candidates_Image_*` | `tests/Agent42.Tests/ModelRouterServiceTests.cs` |
| A lone image survivor is never chosen and makes no call (attached image with no vision chat model; tools on with no tools chat model) | `Candidates_OnlyImageSurvives_NoImageTurn` | same |
| An image winner needs the bar AND to be the largest answer including "cannot tell"; a lower bar cannot break the second clause | `ImageBar_*` | same |
| "Cannot tell" beats a below-bar image winner by falling to the chat rule; zero chat candidates gives null | `ImageBar_CannotTellFallsToChat`, `Decide_NoChatCandidates_Null` | same |
| An image model with no image service on its provider is not a candidate | `ToCandidate_ImageWithoutService_Dropped` | `tests/Agent42.Tests/DecisionModelProviderTests.cs` |
| An image winner below the image bar falls back to the best chat candidate under the sticky rule; at or above it is chosen | `ImageBar_*` | same |
| The current model is never an image model: with `_modelId` an image model the chat winner needs no margin, and an image winner is a one-off | `Sticky_CurrentImageModelIsIgnored` | same |
| The router input carries the previous-reply-was-image line, and only a fixed string (never user text) | `State_PreviousReplyWasImage` | same |
| Image candidates get a derived description; hint and card still win | `Describe_ImageModel_*` | same + `ModelCardSourceTests.cs` |
| `ToCandidate` maps `SupportsImageGeneration` to an image candidate, and other non-chat kinds stay non-chat | `ToCandidate_Image*` | `tests/Agent42.Tests/DecisionModelProviderTests.cs` |
| The request body carries the image-aware instructions and still uses positional keys | `Choose_SendsImageAwareInstructions` | same |
| An Auto turn routed to an image model returns `Routed` then `Image`, and leaves `_modelId`, `DeploymentName` and `AutoModel` unchanged | `AutoImageTurn_DoesNotSwitchTheChatModel` | `tests/Agent42.Tests/ModelRouterGrainTests.cs` |
| The next Auto or explicit text turn after an Auto image turn runs on the original chat model | `AutoImageTurn_NextTextTurnUsesTheChatModel` | same |
| Edit input is this turn's attachment, else the previous turn's image, else none; an older image is never used | `AutoImageTurn_EditInput_*` (uses `ProbeImageGenerator`, which echoes the input) | same |
| Picking the image model by hand still reuses the most recent image anywhere (unchanged) | existing image tests | `AgentGrainTests.cs` |
| A conversation left on an image model then Auto: a chat winner switches off it, and an image winner does not persist a change | `AutoFromAnImageModel_*` | `ModelRouterGrainTests.cs` |
| A USER image in history restricts chat candidates to vision models but the image option remains offered; a generated image alone does not set `HasImages` | `AutoImageTurn_HistoryImages_StillOffersImageModel` (uses an attachment ref as in `AgentGrainTests.cs:1566`) | same |
| Incognito, agent-pinned and in-flight turns do not route to an image model; a decision dropped after the await makes no image turn | `AutoImage_Skips*` | same |
| Image generation failure after routing propagates out of the stream (observed with `Assert.ThrowsAnyAsync` while enumerating), records nothing, and does not fall back to chat | `AutoImageTurn_GenerationFailure_Surfaces` (needs `ProbeImageGenerator.Failure`) | same |
| An unrouted Auto turn on a conversation whose `_modelId` is an image model runs on the default chat model: router failure, null decision, no router, 0 or 1 candidate, incognito | `AutoFromAnImageModel_Unrouted_UsesTheDefaultChatModel` | same |
| A turn skipped for another run in flight does not switch, even from an image model | `AutoFromAnImageModel_InFlight_DoesNotSwitch` | same |
| An image decision dropped because another run started during the router await makes no image turn | `AutoImage_DroppedWhenARunStartsDuringTheRouterCall` (needs the `FakeModelRouter` gate) | same |
| `PreviousReplyWasImage` is true after an Auto or by-hand image turn, false after a chat turn, and survives a no-vision strip | `PreviousReplyWasImage_*` | same |
| A regenerated Auto image turn edits the image before it (documented behaviour) | `AutoImageTurn_Regenerate_UsesThePreviousImage` | same |
| A `Changed` image decision reloads the grain's providers once, and a kept or null decision does not (uses `CountingChatClientFactoryProvider`, already in `tests/Agent42.Tests/FakeModelRouter.cs` from #258) | `AutoImageTurn_ReloadsTheProviders` | `ModelRouterGrainTests.cs` |
| A routed image turn reaches a chat app as an image | `Pipeline_AutoImageTurn_DeliversTheImage` | `tests/Agent42.Tests/ChatTurnPipelineTests.cs` |
| `Routed` for an image turn is emitted every time, with the image model's id and name | `AutoImageTurn_AlwaysLabelled` | `ModelRouterGrainTests.cs` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` on 2026-11-21)

- `branches.protected`: `[main]`: work on `feature/auto-image-routing`, MR only
- `coverage_new_code_pct_min`: 80 (tool: coverlet, `node .agentic/scripts/check-new-code-coverage.mjs`)
- `cognitive_complexity_max`: 15 (S3776 is a build error; `ChatTurnAsync` and `MapModelEndpoints` have both tripped it already)
- `vulnerable_packages`: 0
- `outdated_packages`: report
- `add_policy`: latest-stable (no new package expected)
- Also: `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, `dotnet build Agent42.slnx`

## Out of scope

- Routing for incognito turns, agent chats, fleet, stored agents, email-in and reminders (as ADR 0137).
- Multiple images per turn, size or quality pickers, masks, a cross-conversation gallery (ADR 0020).
- Automatic fallback from a failed image turn to a chat reply.
- A per-model "use in Auto" flag, and any cost estimate or usage-dashboard row for images.
- A tool a chat model could call to make images (rejected in ADR 0020 too).
- Routing to embedding, speech or transcription models.

## Review

Critic verdict on the first draft: no blockers, 3 high, 4 medium, 2 low. Every cited path and symbol existed and the described
behaviour held. All findings were accepted and folded in above:

1. HIGH lone image candidate bypassing the bar: accepted (shortcut now chat-only; image-only survivors return null; test added).
2. HIGH "keeps the current model" landing on the image endpoint when `_modelId` is an image model: accepted (unrouted Auto falls back to
   the default chat model, except for an in-flight skip; `CurrentModelId = null` encodes "none"; tests added).
3. HIGH `AutoImageTurn_GenerationFailure_Surfaces` unwritable with the existing fakes: accepted (`ProbeImageGenerator.Failure`, reset by the
   fixture; observation method stated).
4. MED S3776 risk: accepted; the helper (`ImageModelForTurn`) is now the primary design, with the commit that previously fixed the same
   trip cited as the baseline.
5. MED underspecified `Decide` image-bar semantics and an image model with no image service: accepted (rules written out; provider
   predicate added).
6. MED edit-input and previous-reply gaps: accepted (key not ref; regenerate and photo-then-"cartoon" behaviours documented and one tested).
7. MED `HasImages` premise and missing tests: accepted (premise corrected; Count==1, in-flight-after-await, history-image tests mapped).
8. LOW ADR front matter and egress worst case: accepted (`amends: [0137]`, `extends: [0020]`, worst-case sentence).
9. LOW gotcha line number, labelling slips: accepted.

Rejected: none.
