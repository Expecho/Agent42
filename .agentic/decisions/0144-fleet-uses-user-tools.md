---
id: 0144
date: 2026-11-24
status: accepted
tags: [fleet, tools, selectable-tools, skills, memory, past-chats, push, image-generation, copilot, mcp, egress, share, no-migration]
supersedes: []
amends: [0097, 0099, 0013]
---

# Fleet tasks use the user's skills, memory recall, past chats, push and image generation; Copilot fleet agents get MCP and skills

## Context
Until 2026-11-24 a regular fleet agent got `web_search`, the workspace document tools (ADR 0101) and the user's MCP tools
(`FleetToolCatalog`, ADR 0097 decision 6, ADR 0099). A Copilot fleet agent got none of those, only the canvas, the todo list
and Copilot's built-ins. The user asked for skills, image generation, `read_document` and "all the built-in tools", and for
MCP and skills on Copilot fleet agents too. A fleet run has no conversation, no browser and no `TurnContext`, so the chat's
tool methods can't be reused: they read grain state and `TurnContext.CurrentOrClosed`, which reads `Closed` off-grain.

## Decision
1. **Per chat key, decided at plan time** (the user asked the plan to decide, and it narrows "all the built-in tools" on purpose):

   | Chat key | Fleet | Why |
   |---|---|---|
   | `mcp*` | yes, now on Copilot agents too | user decision |
   | `skills`, `skill:{name}` | yes, regular and Copilot agents | user decision |
   | `memory` | **recall only**, `recall_memory(query)` | no context-provider slot on a declarative fleet agent; a team's chatter must never be retained into long-term memory |
   | `past_chats` | yes | read-only, by userId, needs no conversation |
   | `push_notification` | yes, Web Push only | "notify me when X" is a natural fleet ask |
   | image generation | yes, new `generate_image` | not a chat tool (it is a per-turn route), so a fleet-owned tool writes into the workspace |
   | `read_document` | no | already covered by the fleet's own workspace document tools (ADR 0101) |
   | `scheduling`, `location`, `browser_tools`, `shared_state`, `artifacts`, `read_web_document`, `code_interpreter`, `fleet` | no | each needs a conversation, a browser, a hosted tool kind the fleet refuses (ADR 0097 d4), or is already covered (`delegate_to_team`, the canvas) |

2. **Fleet-owned, off-grain tools.** `FleetSkillTools`, `FleetMemoryTools`, `FleetPastChatTools`, `FleetPushTools` and
   `FleetImageTools` are built once per task by `FleetToolCatalog`, so **every budget lives on the tool instance** (per task):
   past chats `PastChatLimits.MaxCallsPerTurn` calls, pushes `PushToolLimits.MaxPushesPerTurn`, images `FleetImageTools.MaxImagesPerTask`.
   The pure cores are shared, never forked: `PastChatQuery` (ranking, formatting), `PushToolLimits.SanitizeNotificationText`,
   `ReadSkillResourceTool`, `ToolSelection.Merge` (uploads shadow shipped skills).
3. **Skills on regular agents: own `load_skill` + the reused `read_skill_resource`.** The plan's preferred path was to harvest
   `TruncatingSkillsProvider`'s tools off-agent. It isn't possible cleanly: `AIContextProvider.InvokingContext`'s only constructor
   (Microsoft.Agents.AI 1.28.0) takes an `AIAgent` and an `AgentSession`, and a fleet catalog has neither. So `load_skill(name)`
   returns the selected skill's `SKILL.md` cut at `SkillToolLimits.MaxSkillBodyLength` plus a bounded listing of its files, with
   the skill list in its description. There is **no `run_skill_script` at all**, and nothing in fleet code executes a skill file.
   A companion skill (ADR 0126) is exposed only when its tool is a fleet tool: `websearch` with `web_search`, never `scheduledprompt`.
4. **Copilot fleet agents** get the catalog's tools as custom `Tools` (never `AvailableTools`), Copilot's native skill loader
   (`SkillDirectories`, `EnableSkills`) for the selected skills, and the reused `read_skill_resource`, because the fleet's
   Copilot permission policy confines file reads to the workspace. **`FleetCopilotPermissionPolicy` is unchanged.** The
   regular agents' `load_skill` is not passed to Copilot: its native `skill` tool does that.
5. **No new selectable key** (rule 11). Skills, memory, past chats and push reuse their chat keys and `null == all`.
   `generate_image` is gated by capability: present iff the user has an image-generation model. Gating is explicit per family
   (`enabled.Contains(Memory)`, `IsToolEnabled` on the mapped past-chat and push names, `IsSkillEnabled`), never
   `IsToolEnabled` on an unmapped name, which would always pass. The new names are reserved against MCP tools.
6. **Push opens the task.** `PushMessage` gains a trailing optional `Url` (it never crosses a grain boundary, so no `[Id]`);
   the Web Push payload carries `url` only when set, and `sw.js` focuses a window and navigates it to that url, or opens it.
   The fleet sends `/fleet/?task={id}`, the deep link the page already reads. The chat push path is unchanged.
7. **Chat-started tasks (ADR 0115)** need nothing new: none of these tools asks a question.

## Egress (extends ADR 0013) and share links
- Fleet agent text goes to Hindsight as a recall **query** (gated by the `memory` key and the operator's `HINDSIGHT_API_KEY`, ADR 0131). Nothing is retained.
- Fleet prompts go to the user's own image provider (ADR 0020).
- Push title and body go to the Web Push services, as in chat (ADR 0075). Past chats stay internal.
- **Share links (ADR 0120) can now carry private content**: `recall_memory` and `read_past_chat` bring memory and other
  conversations into agents that write the result, canvas pages and workspace files, and `generate_image` writes into the
  shared `images/`. No gating: the user decides what to share, and the share dialog says so in one line.

## User-visible change
`null == all` (ADR 0021) switches all of this on for a user who never narrowed their tools: fleet tasks start making recall
queries, sending pushes and reading past chats without the user doing anything. This matches chat. A task's tool override
(ADR 0099) and the picker on `/fleet/` now list these keys.

## Alternatives considered
- **Reuse the grain's tool methods** — they read grain state and `TurnContext.CurrentOrClosed`; off-grain every budget reads
  exhausted and every call refuses (rule 6).
- **Harvest `TruncatingSkillsProvider` with a stub agent** — keeps one source of the skill tools, but builds a fake `AIAgent`
  around an `[Experimental]` constructor and still advertises `run_skill_script`, which then has to be filtered. A 40-line
  `load_skill` over the same caps is smaller and has no script surface at all.
- **Retain fleet runs into memory** — a team's internal chatter, judge verdicts and retries would become the user's memories.
- **Widen the Copilot read policy to the skill folders** — hands a shell-capable agent read access outside its workspace;
  the narrow `read_skill_resource` was already measured to be enough for the chat kind (ADR 0085).
- **A new `image_generation` selectable key** — would land OFF for anyone who ever pressed Save (rule 11) and gate nothing the
  capability check doesn't already gate.

## Consequences
- Regular and Copilot fleet agents can use the user's skills, memory, past chats, push and image models.
- Out of scope: memory retain, push to the chat-app home chat (ADR 0111), user-overridable per-task caps (ADR 0135).
- Open: a live check that a Copilot fleet agent loads an uploaded skill and reads one of its resources (DevHost can't run the Copilot CLI).
