# Plan: fleet tasks use the user's skills, memory, past chats, push, image generation, and MCP on Copilot agents

planned-against: main @ 24f81ec

## Overview
Fleet agents should use the tools a user has already set up for chat. Today a regular fleet agent gets `web_search`,
the workspace document tools and the user's MCP tools (`FleetToolCatalog`, ADR 0097 d6 / ADR 0099). Copilot fleet
agents get none of those, only canvas, todos and Copilot's built-ins. The user asked (2026-11-24) for skills, image
generation, read_document and "all the built-in tools", and for Copilot fleet agents to get MCP and skills too. This
plan decides, tool by tool, what makes sense in a background run with no browser and no conversation, and builds
those tools as **fleet-owned, off-grain tools**.

## Affected areas (verified against the tree)
- `src/Agent42.Backend/Fleet/FleetToolCatalog.cs`: catalog grows skills, memory recall, past chats, push and image generation
- `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs` (`BuildRunAsync`, ~l.120-155): passes the catalog result (and skill dirs) to the Copilot factory
- `src/Agent42.Backend/Fleet/FleetRun.cs`: carries the selected skill directories for Copilot
- `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs` (`BuildSessionConfig` l.170-188, ctor l.72-81)
- `src/Agent42.Agents/Copilot/ReadSkillResourceTool.cs`: reused as is (static `Create(skills)`, no grain state) for Copilot fleet agents
- `src/Agent42.Backend/Fleet/FleetRegistration.cs`: optional dependencies for the new tools
- Grain files whose cores get extracted for sharing (behaviour unchanged): `src/Agent42.Agents/Search/AgentGrain.PastChatTools.cs`,
  `src/Agent42.Agents/Push/AgentGrain.PushTool.cs` (only if the sanitise/limit code isn't already static in `PushToolLimits`),
  `src/Agent42.Agents/Tools/ToolSelection.cs` (`MergedSkills`, if it must become static)
- Push link (Step 4): `src/Agent42.Agents/Push/IPushNotificationSender.cs` (`PushMessage`), the Web Push sender's payload
  builder (find the `IPushNotificationSender` implementation in Backend), `src/Agent42.Backend/wwwroot/sw.js`
  (`push` + `notificationclick`, l.145-175) **and a `CACHE` bump in `sw.js`** (cross-cutting rule 3)
- Share view (H2): the fleet share dialog in `fleet.js` (one-line notice)
- New: `src/Agent42.Backend/Fleet/FleetSkillTools.cs`, `FleetMemoryTools.cs`, `FleetPastChatTools.cs`, `FleetPushTools.cs`, `FleetImageTools.cs` (flat namespace `Agent42.Backend`, ADR 0040)
- `src/Agent42.Backend/Fleet/FleetPrompts.cs` / planner prompt text if tool guidance is needed (the `{{tools}}` slot is generated from descriptions already)
- `src/Agent42.Backend/wwwroot/fleet/fleet.js` (`fleetToolKey` l.552, picker)
- `src/Agent42.Agents/Push/IPushNotificationSender.cs` (`PushMessage`) only if Step 4 shows a fleet link can't be expressed
- Tests: `tests/Agent42.Tests/FleetToolCatalogTests.cs`, `FleetCopilotAgentFactoryTests.cs`, `FleetCopilotPermissionPolicyTests.cs`, `FleetPageAssetsTests.cs`, `FleetTaskServiceTests.cs`, new `FleetSkillToolsTests.cs`, `FleetPersonalToolsTests.cs` (memory, past chats, push), `FleetImageToolsTests.cs`
- Docs: new ADR (amends 0097 d6 and 0099), `.agentic/gotchas.md`, `CLAUDE.md` feature map row for the fleet

## Context & constraints

### Per-tool decision (made at plan time, the user asked the plan to decide)
| Chat key / tool | Fleet? | Why |
|---|---|---|
| `mcp`, `mcp:{id}`, `mcp:{id}:{tool}` | already, **now also on Copilot agents** | user decision 2 |
| `skills`, `skill:{name}` (shipped + uploaded, ADR 0048/0049) | **yes**, regular and Copilot agents | user decision 1 |
| `memory` (Hindsight) | **yes, recall only**, as a `recall_memory(query)` tool | Fleet agents are declarative YAML (`ChatClientPromptAgentFactory(client, functions)`, `FleetTeamRunner.Planning.cs:147`), so there's no per-agent context-provider slot. **No retain**: a team's internal chatter must not be written into the user's long-term memory. Uses `IHindsightClient.RecallAsync(bank, query)` (package `Hindsight.AgentFramework.Net` 0.1.0), with bank = userId as in `AgentGrain.Memory.cs` (`BuildMemoryProvider`). Absent when no Hindsight key (ADR 0131). |
| `past_chats` (`search_past_chats`, `read_past_chat`) | **yes** | read-only over `IChatSearchIndex` + `IConversationIndexGrain` by userId (`Search/AgentGrain.PastChatTools.cs`); needs nothing of a conversation |
| `push_notification` | **yes** | "notify me when X" is a natural fleet ask. Web Push only (see Out of scope) |
| image generation | **yes, new `generate_image` tool** | In chat it is NOT a tool: it is a per-turn route (`ImageGeneration/AgentGrain.ImageTurn.cs`, `AgentGrain.cs:627-636`). The fleet tool generates with one of the user's image-capable models (`IChatClientFactory.GetImageGenerationService`) and **writes the bytes into the task workspace** (`images/…`), returning the path. No new selectable key (see rule 11 below): present iff the user has an image-capable model. |
| `read_document` (chat RAG) | **no, already covered** | It reads one conversation's documents (`Documents/AgentGrain.ReadDocumentTool.cs`). The fleet has its own `search_documents`/`read_document` over workspace files (ADR 0101), and `read_document` is a reserved name in `FleetToolCatalog.Usable` |
| `web_search` | already | |
| `scheduling` | no | Registers reminders on a conversation grain and re-runs a prompt there; a fleet task has no conversation |
| `location`, `browser_tools` | no | client (browser-run) tools; a fleet run has no broker |
| `shared_state`, `artifacts` | no | conversation state; the fleet has its own canvas (ADR 0102) |
| `read_web_document` | no | ingests into conversation RAG state; fleet web pages come via search plus workspace documents |
| `code_interpreter` | no | hosted provider tool; the fleet refuses hosted YAML tool kinds (ADR 0097 d4, `CheckAgent` in `FleetTeamRunner.Planning.cs`). Copilot fleet agents already have a shell |
| `fleet` (`start_fleet_task`) | no | the fleet has `delegate_to_team` |

Tell the user this table at approval. They said "all the built-in tools", and this plan narrows that on purpose.

### Rules this plan must respect
- **Rule 6 / TurnContext (gotchas "Per-run state is a `TurnContext`…").** Never reuse the grain's tool methods: they
  read grain state and `TurnContext.CurrentOrClosed`, and a fleet run would see `Closed` (budgets exhausted, refusals).
  Build fleet-owned `AIFunction`s whose **per-task budgets live on the tool instance**. `BuildRunAsync` builds them
  once per task, so the instance is the task's. Do NOT call `TurnContext.RunInAsync` from fleet code.
- **Rule 5 / truncation.** Each new tool's return is bounded: reuse the chat's constants where they exist
  (`ChatSearchLimits`, `PushToolLimits`, `McpToolLimits`/`WebToolLimits` style). Memory recall gets a cut via
  `FleetTeamRunner.Cut`. Skill text uses the same cap as `TruncatingSkillsProvider`. `generate_image` returns a path,
  never bytes.
- **Rule 11 / new selectable keys.** This plan adds **no** new `ToolCatalog` key. Skills, memory, past chats and push
  reuse existing keys and their `null == all` semantics. `generate_image` is gated by capability (an image model
  exists), like the document tools are gated by presence. Record that in the ADR.
- **Rule 13 / egress.** New content flows, all to existing per-user or operator-gated providers, recorded as an
  ADR 0013 extension in the new ADR: (a) fleet agent text → Hindsight as a recall **query** (gated by the `memory`
  key and the operator `HINDSIGHT_API_KEY`); (b) fleet prompts → the user's own image provider; (c) push title/body →
  Web Push services (same as chat, ADR 0075). Past chats stay internal.
- **Chat-started tasks (ADR 0115, gotcha "A chat-started fleet task answers its own questions…").** None of the new
  tools opens a question, so no new auto behaviour is needed. Say so in the ADR. `start_fleet_task` already refuses
  from incognito, so no fleet task comes from an incognito turn.
- **Copilot (ADR 0097 d7, gotchas "A fleet Copilot `SessionConfig` must NOT set `AvailableTools`", "The fleet's Copilot
  client is NOT ADR 0085's client", "The Copilot runtime calls custom tools OFF the grain's scheduler").** Add to
  `Tools` only, never `AvailableTools`. Keep the change inside `FleetCopilotAgentFactory`/`FleetCopilotHost`. Fleet
  tools are off-grain, so no `SchedulerBoundAIFunction`. Names must pass `CopilotLimits.IsValidToolName`
  (`FleetToolCatalog.Usable` already applies the same regex). `PermissionRequestCustomTool` is already approved
  (`FleetCopilotPermissionPolicy.cs:33`).
- **Copilot skills.** Mirror the chat kind (`AgentGrain.Copilot.cs:58-78, 118-121`, `CopilotAgentFactory.BuildSessionConfig`
  l.246-247): `SkillDirectories` = the selected skill paths, `EnableSkills = true`, keep `IncludedBuiltinSkills = []`.
  Skill directories are outside the task workspace, and the fleet policy rejects reads there
  (`FleetCopilotPermissionPolicy.cs:34-35`). This was **already measured for the chat kind** (critic H3,
  `src/Agent42.Agents/Copilot/ReadSkillResourceTool.cs` doc comment): with confined file tools, the model can't read
  a skill's resources and asks the user to paste them. The chat's fix is the `read_skill_resource` custom tool
  (`ReadSkillResourceTool.Create(skills)`, `AgentGrain.Copilot.cs:118-121`). **The fleet reuses it, and
  `FleetCopilotPermissionPolicy` does NOT change.** Widening the read policy was the riskier alternative, and it was rejected.
- **Uploaded-skill security (gotcha "Uploaded skills: the framework's own limits…").** The script ban and resource
  rules live in `TruncatingSkillsProvider.ForSkillPaths`. Fleet skill tools must keep the same guarantees: no script
  execution, ever.
- **ADR 0099's picker.** `fleet.js` is a classic script outside the ES-module graph and the SW (ADR 0041, `sw.js` skips
  `/fleet`), so it needs no `CACHE` bump by itself. **But Step 4 changes `sw.js`**, so the MR bumps `CACHE` anyway. The override's "send only when changed" behaviour stays. The `skills` wildcard must be expanded
  before the picker sees it (rule 11, ADR 0048), the same as `mcp`.
- Rule 9: S3776 ≤ 15, so put each tool family in its own class.
- **Share links expose what these tools pull in (critic H2).** ADR 0120's public share link serves the request, result,
  canvas and workspace files. `recall_memory` and `read_past_chat` bring private memory and other conversations into
  fleet agents, and those agents write results, canvas pages and files. `generate_image` writes into `images/`, which is
  shared too. Record this in the new ADR as part of the egress section. The share dialog shows one line: "The shared
  result can include content your agents read from your memory and past chats." No gating, because the user decides
  what to share.
- **`null == all` switches these on for everyone with no stored selection (critic M3).** Before this change the fleet
  ignored `memory`, `push_notification`, `past_chats` and skills. After it, a user who never narrowed their tools gets
  recall queries to Hindsight, push notifications and past-chat reads in fleet tasks without doing anything. This
  matches chat (ADR 0021). Record it in the ADR as a user-visible change, and tell the user at approval.
- **Gating is explicit per tool (critic M2).** `ToolCatalog.IsToolEnabled` keeps any name missing from `ToolNameToKey`
  (`ToolCatalog.cs` ~l.441). `recall_memory` and `generate_image` are unmapped. So each new family is gated by its own
  check in `FleetToolCatalog`: `enabled.Contains(ToolCatalog.Memory)`, `IsToolEnabled` for the mapped past-chat and
  push names, `IsSkillEnabled` for skills, and capability for `generate_image`. Never by `IsToolEnabled` on an unmapped
  name. `ToolNameToKey` stays unchanged, since chat has no such tools.
- **Companion skills (critic M1).** `ToolCatalog.CompanionSkills` turns on `scheduledprompt` when `scheduling` is
  selected and `websearch` when `web_search` is. The fleet has no `schedule_prompt`, so **exclude a companion skill
  whose tool the fleet doesn't have** (today `scheduledprompt`). Keep `websearch` only when the fleet's `web_search` is
  present. This applies to both regular and Copilot agents.

## Steps

**Step 0 — Branch + ADR.** `git switch -c feature/fleet-selectable-tools` from `main`. Run `/decide` for a new ADR,
"Fleet tasks use the user's skills, memory recall, past chats, push and image generation; Copilot fleet agents get MCP
and skills" (**amends 0097 d6 and 0099**, egress extension of 0013). Contents: the per-tool table, the recall-only
choice, the capability-gated `generate_image`, and no new selectable key.
Verify: the ADR file exists; `dotnet test --filter "FullyQualifiedName~Adr"` (or whichever test guards ADR front-matter) passes.

**Step 1 — Skills for regular fleet agents (`FleetSkillTools`).** Spike first (`/spike-dotnet`, pinned to the repo's
`Microsoft.Agents.AI` version): can `TruncatingSkillsProvider.ForSkillPaths(paths)` be invoked once off-agent to
harvest its `AIContext.Tools` (`load_skill`, `read_skill_resource`) and `Instructions` (the `<available_skills>` list)
as standalone `AIFunction`s? Known traps (critic M4): the `InvokingContext` constructor is `[Experimental]` (MAAI001,
already suppressed in `TruncatingSkillsProvider`) and may need an `Agent`/`Session`. `InvokingAsync` accumulates onto
the context it's given, so pass a fresh empty one, as `TruncatingSkillsProvider.ProvideAIContextAsync` does.
- **If yes (preferred, keeps one source of the security controls):** expose `load_skill` and `read_skill_resource`
  with the skill list appended to `load_skill`'s description (bounded). **Filter out `run_skill_script`**, which is
  advertised regardless. A test pins that filter (`Skills_NoRunScript`).
- **If no:** `read_skill_resource` = `ReadSkillResourceTool.Create(skills)` (reused), plus a fleet-owned
  `load_skill(name)` that returns `SKILL.md` cut at the same cap the chat uses (`SkillToolLimits.MaxSkillBodyLength`;
  verify the name), with the skill list in its description. Record which path was taken in the ADR.
Exclude companion skills whose tool the fleet lacks (see Constraints).
Skill set = `MergedSkills` logic (uploads shadow shipped, `ToolSelection.cs:44-58`) using
`IUserSkillProvider.GetForUserAsync(userId)`, gated by `ToolCatalog.IsSkillEnabled`. Extract the merge into a shared static if it
can't be called off-grain. Add `load_skill`, `read_skill_resource`, `run_skill_script` and every other new tool name
(`recall_memory`, `search_past_chats`, `read_past_chat`, `send_push_notification`, `generate_image`) to `Usable`'s
reserved set, so an MCP tool can't take one.
Files: `FleetSkillTools.cs`, `FleetToolCatalog.cs`, `FleetRegistration.cs`. Tests: `FleetSkillToolsTests.cs`.
Verify: `dotnet test tests/Agent42.Tests --filter "FullyQualifiedName~FleetSkillTools|FullyQualifiedName~FleetToolCatalog"`.

**Step 2 — Memory recall (`FleetMemoryTools`).** `recall_memory(query)` → `IHindsightClient.RecallAsync(userId, query)`,
cut to a bound. Present only when `HindsightIntegration.Client` is non-null and the selection enables `memory`.
No retain call anywhere in fleet code. Tests: `FleetPersonalToolsTests.cs` (absent without client, absent when deselected,
recall only (a fake client records zero RetainAsync), output cut).
Verify: `dotnet test … --filter FullyQualifiedName~FleetPersonalTools`. *Parallelizable with 1, 3, 4, 5 in worktrees.*

**Step 3 — Past chats (`FleetPastChatTools`).** `search_past_chats` / `read_past_chat` with the chat's argument
contracts, ranking and limits, but a **per-task call budget** on the instance (reuse the chat's per-turn constant as the
per-task cap unless that is clearly too low; document it). Extract the shared core from `Search/AgentGrain.PastChatTools.cs`
into a static/helper usable by both, so the ranking isn't forked. The grain keeps its `TurnContext` budget; only the
pure core moves. Tests in `FleetPersonalToolsTests.cs`: user-scoped (another user's conversations are never returned),
budget refusal, output bounded. The existing past-chat grain tests (`grep -l "search_past_chats\|PastChat" tests/`)
must stay green without edits. *Parallelizable.*

**Catalog plumbing (do in Step 1, the others build on it; critic L1).** `FleetToolCatalog` gets the new optional
dependencies through its constructor (`IUserSkillProvider?`, `HindsightIntegration?`, `IChatSearchIndex?`,
`IPushNotificationSender?`, and the per-user chat-client factory for image models). `BuildAsync` gains the task id
(push link, per-task budgets) and the `FleetWorkspace` (image writes), and returns the selected skill list too, so Copilot
can get `SkillDirectories`. Keep the existing sync `Build(selection, request, servers, interaction, documentTools)`
overload working for the current tests by adding optional parameters, so `FleetToolCatalogTests` doesn't churn.

**Step 4 — Push (`FleetPushTools`).** `send_push_notification(title, body)` reusing `PushToolLimits` sanitising and caps,
a per-task budget (`MaxPushesPerTurn` applied per task), `IUserPushGrain.GetAllAsync` device check, and `IPushNotificationSender`.
The tap must open the task. Today `sw.js` only knows `conversationId`: it posts `open-conversation` to an open window,
or opens `/` (`sw.js:157-175`). So: add an optional trailing `Url` to `PushMessage` (check its serializer attributes
first; if it crosses a grain boundary, it needs a trailing `[Id(n)]` with a default). The sender's payload carries `url`
when it's set. `sw.js` stores it in `notification.data`. On click, when `url` is set, focus a window and navigate it,
or open the url (`/fleet/?task={id}`; use the deep-link form the fleet page already accepts, ADR 0115), and skip the
`open-conversation` message. **Bump `CACHE` in `sw.js`.** The chat push path stays unchanged (no url). Never log
title/body (mirror `PushTool_DoesNotLogNotificationText`).
Tests: no devices → model told; budget; no text logged; `PushMessage.Url` = the task link; the sender payload includes
`url` only when set; a `sw.js` source-text guard for the url branch (find the existing sw.js asset test, e.g. the one
pinning `CACHE`). Existing chat push tests stay green. *Parallelizable.*

**Step 5 — Image generation (`FleetImageTools`).** `generate_image(prompt, model?)`: the user's image-capable models
are the per-user catalog filtered by the image-generation capability (the same capability check `AgentGrain` uses before
routing to `GenerateImageTurnAsync`). The description lists their ids, and the default is the first in catalog order.
`GetImageGenerationService(id)` returns the generator. Not counted by the task's token budget (it isn't a chat call),
so the per-task image cap is the bound. Refuse a result larger than the attachment size limit the chat uses for
generated images, if there is one. Bytes are
written into the task workspace under `images/` (unique name). Return the relative path and media type only. Per-task
cap (a constant). Respect the workspace sync gotcha ("The fleet workspace sync DELETES stored files that aren't on
local disk"): write to local disk through the workspace API, as other workspace writers do. Tests: absent without an
image model; file lands in the workspace; bytes never in the result; cap. *Parallelizable.*

**Step 6 — Copilot fleet agents get the catalog tools and skills.** Pass `FleetRun.Tools` (the catalog's output:
web, MCP, documents, and the new tools) and the selected skill directories into `FleetCopilotAgentFactory` and set
`Tools = [.. canvas, .. todos, .. runTools (minus the regular-agent load_skill), ReadSkillResourceTool.Create(skills) when skills are selected]`,
`SkillDirectories`, `EnableSkills` (only when non-empty). Copilot loads skills natively. Never set `AvailableTools`.
`FleetCopilotPermissionPolicy` is unchanged.
Tests: `FleetCopilotAgentFactoryTests` (`SessionConfig_CarriesCatalogTools_KeepsBuiltins`, `SessionConfig_SkillDirectories_OnlyWhenSelected`,
`SessionConfig_ReadSkillResource_OnlyWithSkills`, `AvailableTools` stays null). Live check after deploy (DevHost can't
run the Copilot CLI): a Copilot fleet agent loads an uploaded skill and reads one of its resources. Record the result in the MR.

**Step 7 — Fleet picker.** In `fleet.js`, widen `fleetToolKey` to `web_search`, `mcp:*`, `skill:*`, `memory`, `past_chats`,
`push_notification`; expand a stored `skills` wildcard into `skill:{name}` for display, as for `mcp`. A `memory` row
appears only when `/api/tools` lists it (ADR 0131). Keep "send only when changed". Update the ADR 0099 header text
("Tools: N selected"). Add the share-dialog notice (Constraints, H2). Tests: `FleetPageAssetsTests` source-text guards for the new key filter and wildcard expansion;
`FleetToolCatalogTests` for server-side gating of each new key under override, profile default and `null == all`.

**Step 8 — Docs.** `/gotcha` for anything non-obvious found (e.g. the skill harvest, the Copilot skill-read permission).
Update the CLAUDE.md feature map fleet row with the new ADR number. Update `FleetToolCatalog`'s doc comment. `/sync-docs`.
Verify: `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
`node .agentic/scripts/check-new-code-coverage.mjs`.

## Acceptance criteria → tests
| AC | Test | File |
|---|---|---|
| Selected skills reach regular fleet agents; deselected don't; uploads shadow shipped; no `run_skill_script` | `Skills_Selected_Exposed`, `Skills_Deselected_Absent`, `Skills_UploadShadowsShipped`, `Skills_NoRunScript` | `FleetSkillToolsTests.cs` |
| Skill text is capped | `LoadSkill_LongBody_Cut` | `FleetSkillToolsTests.cs` |
| Memory: recall only, gated by key and operator key | `Memory_NoClient_Absent`, `Memory_Deselected_Absent`, `Memory_NeverRetains`, `Memory_ResultCut` | `FleetPersonalToolsTests.cs` |
| Past chats are user-scoped and budgeted per task | `PastChats_OtherUser_NotReturned`, `PastChats_BudgetPerTask` | `FleetPersonalToolsTests.cs` |
| Push: budget per task, no text logged, opens the task | `Push_BudgetPerTask`, `Push_DoesNotLogText`, `Push_LinksToTask`, `Push_NoDevices_ToldModel` | `FleetPersonalToolsTests.cs` |
| Image: workspace file, no bytes in result, absent without image model, capped | `Image_WritesWorkspaceFile`, `Image_NoImageModel_Absent`, `Image_Cap` | `FleetImageToolsTests.cs` |
| Excluded keys never produce a fleet tool (scheduling, location, browser_tools, shared_state, artifacts, read_web_document, fleet) | `Build_ExcludedChatKeys_NoTools` | `FleetToolCatalogTests.cs` |
| Override/default/null==all gate each new key | `Build_NewKeys_GatedByOverride`, `…_ByProfileDefault`, `…_NullMeansAll` | `FleetToolCatalogTests.cs` |
| New names reserved against MCP collisions | `Build_McpToolNamedLoadSkill_Skipped` | `FleetToolCatalogTests.cs` |
| Copilot agents get catalog tools + skills, keep built-ins, no AvailableTools | `SessionConfig_CarriesCatalogTools_KeepsBuiltins`, `SessionConfig_SkillDirectories_OnlyWhenSelected` | `FleetCopilotAgentFactoryTests.cs` |
| Copilot agents get `read_skill_resource` only with skills; policy unchanged | `SessionConfig_ReadSkillResource_OnlyWithSkills` | `FleetCopilotAgentFactoryTests.cs` |
| Companion skill for a tool the fleet lacks is not exposed | `Skills_CompanionWithoutTool_Excluded` | `FleetSkillToolsTests.cs` |
| Push tap opens the fleet task; chat push unchanged | `Push_PayloadUrl_OnlyWhenSet`, `Sw_NotificationClick_OpensUrl` (source-text) | push sender tests / sw.js asset tests |
| Share dialog warns about memory/past-chat content | `FleetShare_NoticeMentionsMemory` (source-text) | `FleetPageAssetsTests.cs` |
| Picker offers new keys and expands `skills` | `FleetPicker_OffersSkillsMemoryPastChatsPush`, `FleetPicker_ExpandsSkillsWildcard` | `FleetPageAssetsTests.cs` |
| Copilot skill load works live | manual live check (Step 6) | n/a: record the result in the MR |

## Quality gates (from `.agentic/quality-gates.yaml` at plan time)
| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at build |
| `vulnerable_packages` | 0 | CI package-freshness |
| build / tests / `dotnet format` | pass | CI |

## Out of scope
- Memory **retain** from fleet runs (deliberate, see above).
- Push delivery to the chat-app home chat (`DeliverNotificationToChatAsync`, ADR 0111): Web Push only. Can follow later.
- Scheduling, client tools, artifacts, shared state, `read_web_document`, `code_interpreter`, `start_fleet_task` in the fleet.
- Per-agent tool choice by the user (unchanged from ADR 0099).
- Making the new per-task caps user-overridable in fleet Advanced settings (ADR 0135).
- The fleet analyzer and the dream keep their fixed tools.

## Review
Critic pass on 2026-11-24: 3 high, 5 medium, 3 low findings. All were accepted and are folded in above:
- H1 push link: decided to add `PushMessage.Url` plus a `sw.js` click branch, with a `CACHE` bump (Step 4).
- H2 share link exposure: ADR records it, and the share dialog shows a notice (Constraints, Step 7).
- H3 Copilot skill reads: reuse `ReadSkillResourceTool`. The policy change was dropped (Constraints, Step 6).
- M1 companion skills excluded; M2 explicit per-tool gating; M3 `null == all` behaviour change recorded; M4 spike
  traps and a concrete fallback; M5 grain extractions listed, with regression tests named.
- L1 catalog plumbing described; L2 image model enumeration, budget and size bound.
- **L3 (JS has no coverage gate, only source-text guards): accepted as is.** The repo has no JS coverage tool, and
  ADR 0050 forbids a gate nothing measures.

## Implementation divergences (2026-11-24)
- **Step 1 took the fallback.** `AIContextProvider.InvokingContext`'s only constructor (Microsoft.Agents.AI 1.28.0) takes an
  `AIAgent` and an `AgentSession`, so `TruncatingSkillsProvider` can't be harvested off-agent. `FleetSkillTools` has its own
  `load_skill` (SKILL.md + a file listing, capped at `SkillToolLimits.MaxSkillBodyLength`) and reuses `ReadSkillResourceTool`.
  There is no `run_skill_script` at all, so `Skills_NoRunScript` checks absence plus the reserved name. Recorded in ADR 0144.
- **Skills need the skill provider.** A host without `IUserSkillProvider` gets no skills, shipped ones included, so the existing
  `FleetToolCatalogTests` stay unchanged. In production the provider is always registered.
- **No grain extraction for past chats or push.** The pure cores were already static (`PastChatQuery`, `PushToolLimits`). Only
  `ToolSelection.MergedSkills` gained a static `Merge`.
- **`FleetRun` does not carry skills.** `BuildRunAsync` passes the catalog's `FleetToolSet` (tools + skills) straight into
  `FleetCopilotAgentFactory`'s constructor. `read_skill_resource` reaches Copilot through the catalog tools, and only `load_skill` is filtered out.
- **Image models are read from the user's catalog in `BuildRunAsync`** (`ImageModelsAsync`), because the run's model list holds tool-capable models only.
- **Share notice is in `fleet/index.html`** (the dialog's static markup), not `fleet.js`.
- **Push tap (critic finding):** `sw.js` reuses only a window already on `/fleet/`, otherwise it opens a new one, so a chat window is
  never navigated away.
- **Per-task caps:** past chats 10 (`PastChatLimits.MaxCallsPerTurn`), push 3 (`PushToolLimits.MaxPushesPerTurn`), images 5
  (`FleetImageTools.MaxImagesPerTask`), image size 10 MB (the chat's attachment limit).
- **Still open:** live check that a Copilot fleet agent loads an uploaded skill and reads one of its resources (after deploy).
