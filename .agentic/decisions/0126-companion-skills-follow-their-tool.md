---
id: 0126
date: 2026-11-14
status: accepted
tags: [skills, tools, ux, picker, no-migration]
supersedes: []
extends: []
amends: [0048, 0049]
---

# The shipped `websearch` and `scheduledprompt` skills follow their tool's checkbox

## Context
ADR 0048 made every skill, shipped or uploaded, its own `skill:{name}` picker row. For the two shipped skills that
produced a second, redundant switch: `websearch` is the usage guidance of **Web search** (`web_search`) and
`scheduledprompt` is the guidance of **Scheduling** (`schedule_prompt`, `cancel_*`). The user could tick the tool and
leave its skill off (guidance missing), or tick the skill and leave the tool off (the model is advertised a tool it
does not have). The user asked for the two skills to disappear from the picker and to turn on and off with the
Web search and Scheduling settings.

## Decision
1. `ToolCatalog.CompanionSkills` maps a skill name to the tool key it accompanies: `websearch` → `web_search`,
   `scheduledprompt` → `scheduling`. Names are matched case-insensitively.
2. `ToolCatalog.IsSkillEnabled(name, enabled)` — the one gate, shared by the chat composition and the Copilot one —
   returns, for a companion, **`enabled.Contains(toolKey)` and nothing else**. Neither the bare `skills` wildcard nor a
   `skill:{name}` key enables one.
3. A companion has no picker row and mints no key: `GET /api/tools` omits it, `ToolSelection.ExpandSkillsWildcard`
   does not emit it, and the upload auto-enable (`AutoEnableUploadedSkillsAsync`) does not append it.
4. The rule is by **name**, so an upload that shadows a shipped skill (ADR 0049) is still that tool's companion.
5. Ordinary uploaded skills are unchanged: `skill:{name}` rows, the `skills` wildcard, auto-enable on upload.

## Alternatives considered
- **Hide the rows only (picker-side filter).** Rejected: the gate would still read `skill:websearch`, so a
  Save from the new picker would drop the key and silently turn the guidance off for everyone who had ever saved.
  The switch has to move in the gate, not the UI.
- **Let the `skills` wildcard also enable a companion.** Rejected: `skills` alone would then advertise guidance
  for a tool the selection does not carry, which is the exact mismatch this ADR removes.
- **Add a `Companion` field to `SKILL.md` frontmatter.** Rejected for now: two shipped skills, a static map, and
  uploads must not be able to declare themselves tied to a built-in tool.

## Consequences
- **No migration, no new persisted field.** A stored `skill:websearch` / `skill:scheduledprompt` is inert (the gate no
  longer reads it); it clears the next time the user saves from the picker. `Sanitize` still passes it through.
- **The skills provider (`load_skill`) is now present whenever Web search or Scheduling is selected**, not only
  when a skill row is ticked. There is no longer a way to keep those two tools on and their guidance off.
- **Residual: the `websearch` description names `read_web_document`.** The description sits in the system prompt
  whenever Web search is on, so with `read_web_document` deselected the model still reads that name in guidance text.
  The tool itself is correctly absent; the prohibition the model obeys lives in the tool descriptions (see gotchas),
  so this is a wording issue, not a boundary one. Splitting the skill is the fix if it matters.
- **A future shipped skill needs a decision:** a companion (add it to `CompanionSkills`) or an ordinary row (ADR 0048's
  "silently OFF for anyone who ever saved" trap applies to it).
- Amends ADR 0048 (shipped skills no longer have `skill:{name}` rows) and ADR 0049 (a shadowing upload follows the
  tool key, not its own row).

## Evidence
`src/Agent42.Agents/Tools/ToolCatalog.cs` (`CompanionSkills`, `IsSkillEnabled`), `ToolSelection.cs`
(`ExpandSkillsWildcard`), `src/Agent42.Backend/Tools/AgentApi.Tools.cs`, `src/Agent42.Backend/UserSkills/AgentApi.UserSkills.cs`;
`ToolCatalogTests` (`IsSkillEnabled_CompanionSkillFollowsOnlyItsToolKey`), `AgentGrainTests`
(`CompanionSkill_FollowsItsToolKey_*`, `LegacySkillsWildcard_DoesNotEnableCompanionSkills_ButTheirToolKeysDo`),
`ToolSelectionApiTests` (`Tools_Catalog_IncludesPerSkillKeys`).
