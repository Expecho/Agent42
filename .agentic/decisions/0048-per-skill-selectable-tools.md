---
id: 0048
date: 2026-08-24
status: accepted
tags: [skills, tools, ux, no-migration, per-deployment]
amended_by: [0049, 0126]   # 0049 (per-deployment → per-user; the "no grain field" clause); 0126 (the two shipped skills are tool companions with no picker row)
amends: [0021]   # 0021 (static-catalog scope + the skills gate only — see "Amends")
---

# Title: Per-skill selectable tools — `skill:{name}` keys with a `skills` wildcard

> **Amended by [ADR 0049](0049-upload-agent-skills.md) (2026-08-25) — scope of the skills
> catalog, and one mechanism claim.** Two statements below are now false and are struck through
> where they appear: the skills portion of `GET /api/tools` is **no longer per-deployment** (it is
> the shipped set overlaid with each user's uploaded skills, an upload of the same name winning),
> and the Decision's claim that the wildcard expansion "needs no grain field … no `OnActivateAsync`
> ordering hazard" held only while skills were process-wide — `ExpandSkillsWildcard` now reads a
> `_userSkills` field resolved on activation. The `skill:{name}` key shape, the `skills` wildcard,
> the `null == all` sentinel and the per-skill gate are all unchanged, and this ADR stays
> `status: accepted`.

> **Amends [ADR 0021](0021-selectable-tools-three-layer.md), following the precedent of
> [ADR 0026](0026-per-mcp-server-selectable-tools.md).** Two things are re-scoped, not
> overturned: 0021's "disabling `skills` drops the whole `AgentSkillsProvider`" becomes
> per-skill (`skill:{name}`) with the `skills` key repurposed as an all-skills **wildcard**;
> and 0021's "`ToolCatalog` is the **static** single source of truth" is re-scoped a
> **second** time — the skills portion of `GET /api/tools` is now disk-derived and
> ~~**per-deployment** (the interesting difference from ADR 0026's MCP portion, which is
> per-user)~~ **per-user since [ADR 0049](0049-upload-agent-skills.md): shipped ⊕ the user's
> uploads**, while the static keys stay authoritative and centralised in `ToolCatalog`.
> ADR 0021 and ADR 0026 both stay `status: accepted`. No state migration (the wildcard
> preserves every existing selection).

## Context
ADR 0021's tool picker showed **one** "Skills" checkbox that turned the entire
`AgentSkillsProvider` on or off — every shipped skill or none. Users want to pick
**individual** skills: use `websearch` in this chat, not `scheduledprompt`.

This is the exact analogue of ADR 0026, applied to the file-based skills in
`src/Agent42.Agents/Skills/`. The same two facts shape it: `EnabledTools` uses the
`null == all` sentinel (any migration that reinterprets stored selections is a regression
risk), and the selectable set can no longer be fully static.

Three facts about the pinned `Microsoft.Agents.AI@1.13.0` shape the mechanism. All were
**measured** with a throwaway `dotnet run app.cs` outside the repo tree — and the first two
**replace claims an earlier draft of this ADR got wrong**, which is recorded here rather than
quietly corrected, because a `.agentic/` claim is load-bearing only if it is falsifiable:

1. **`AgentFileSkillsSource` yields `AgentFileSkill`, whose public `Path` is the directory the
   skill was discovered in.** One enumeration of the root therefore gives both halves of the
   `(directory ↔ frontmatter name)` pair. *An earlier draft claimed the root enumeration
   "returns skills with no path attached", and justified a per-directory scan on that basis.
   That was wrong* — the type is public and documented, and the per-directory scan it
   motivated carried a real latent bug: because the source **recurses**, a nested
   skill one level deeper (a `category` directory holding `foo`, whose `SKILL.md` sits two levels below
   the root) would have been recorded under the *category* directory, making two
   skills in one category mutually inseparable in the gate. `SkillCatalog.Discover` enumerates
   the root once and reads `AgentFileSkill.Path`; a test pins the nested case.
2. **Filtering does not require the builder.** `AgentSkillsProviderBuilder.UseFilter` really is
   unusable — `Build()` throws `InvalidOperationException: "File-based skill sources require a
   script runner"` for any file source, and supplying a runner would introduce script-execution
   plumbing this app deliberately does not have. But the builder is not the only route:
   **`FilteringAgentSkillsSource(source, Func<AgentSkill, AgentSkillsSourceContext, bool>)` is
   public and works without a script runner**, and composes with the public
   `AgentSkillsProvider(AgentSkillsSource, AgentSkillsProviderOptions, …)` ctor (measured — the
   filtered source returned exactly the predicate's skill and the provider constructed). *An
   earlier draft rejected filtering outright on the script-runner finding; that rejection was
   too broad.* See "Alternatives considered" for why the path list still won.
3. **A `SKILL.md` whose frontmatter `name` differs from its directory name is silently
   omitted** — the source returns the other skills and does not throw. So the framework
   enforces `name == directory name` per directory, and a mismatched skill is **invisible in
   the picker with no error anywhere**. See the gotcha.

Filtering by **path** is therefore a change of *argument* to the existing
`AgentSkillsProvider(IEnumerable<string> paths, …)` call, not a change of approach: a path
pointing at a single skill's directory yields exactly that skill, and a missing directory or an
empty list yields `[]` without throwing.

## Decision
Each shipped skill is individually selectable as a per-skill key **`skill:{name}`**, where
`{name}` is the skill's frontmatter `name`. The former group key `Skills` (`"skills"`) is
**repurposed into a wildcard "all skills" meta-key**:

- It stays in `AllSelectableToolKeys` (so `null == all` and any legacy selection carrying
  literal `"skills"` keep enabling **every** skill — **zero migration**) but is **removed
  from `Descriptors`** (no longer a rendered checkbox).
- A new **`SkillCatalog`** discovers the shipped skills once per process from
  `AppContext.BaseDirectory/Skills` in a **single enumeration**, reading each
  `AgentFileSkill.Path` for the directory half of the pair, and exposes
  `SkillInfo(Name, Description, Path)` ordered by name (ordinal, so the picker is stable) and
  de-duplicated first-wins. Discovery is **defensive**: a missing or unreadable root, an
  unreadable directory or a malformed `SKILL.md` yields a shorter list, never an exception —
  a broken skill file must not take down every conversation on the silo (see Consequences for
  why `Lazy` makes that guarantee load-bearing rather than merely tidy).
- `BuildAgent` gates **per skill**: a skill's directory is handed to the provider when
  `SupportsTools && (enabled.Contains(Skills) || enabled.Contains("skill:{name}"))`
  (`ToolCatalog.IsSkillEnabled`), and the provider is **constructed only when the resulting
  path list is non-empty**. An empty list would build a provider advertising no skills while
  still handing the model a `load_skill` schema — precisely the trap ADR 0021's
  "`AgentSkillsProvider` is a TOOL" rule closes. The RAG `TextSearchProvider` arms are
  unchanged: it is not a tool and stays on whenever documents are present.
- `GET /api/tools` appends one `ToolDescriptor("skill:{name}", name, false)` per shipped
  skill. ~~Unlike the MCP portion this is **per-deployment, not per-user** — skills ship with
  the image, so every user sees the same set and there is no management/enable flag.~~
  **Superseded by [ADR 0049](0049-upload-agent-skills.md):** like the MCP portion it is now
  per-user — the shipped set overlaid with the skills that user has uploaded.
- Unlike `Mcp`, the `Skills` key **keeps** its three `ToolNameToKey` entries (`load_skill`,
  `read_skill_resource`, `run_skill_script`): those names are static and known, so the
  "every registered tool maps to a catalog key" guard still covers them through this key.

**The wildcard must be expanded before it reaches the picker** — the identical hazard ADR
0026 records, now with a second instance. `GetEnabledToolsAsync` composes
`ExpandSkillsWildcard(ExpandMcpWildcard(effective))`. Each expansion appends at the tail, so the
projection is `[static keys…, mcp:{id}…, skill:{name}…]` — deliberately **not** the order
`Sanitize` produces (static, then `skill:`, then `mcp:`). The two are independent projections of
an unordered set, one for the picker and one for persistence, and nothing requires them to agree;
what does depend on the order is the pair of order-sensitive `Assert.Equal` guards, which run with
zero MCP servers and so pin only the `[static…, skill:{name}…]` tail. Unlike the MCP
one this needs **no grain field**: `SkillCatalog` is static and process-wide, so there is no
`OnActivateAsync` ordering hazard. `renderToolChecks` correspondingly treats a stored bare
`skills` as "all skills checked", and its `cb.checked` is now a **three-way** OR — both
wildcard terms are load-bearing and neither may be removed.

## Alternatives considered
- **Keep the single `skills` group key** — rejected: it is exactly the all-or-none the user
  wants gone.
- **Filter through `AgentSkillsProviderBuilder.UseFilter`** — rejected: `Build()` demands a
  script runner for any file source (measured), and adding one would bring script-execution
  plumbing this app deliberately lacks.
- **Filter through `FilteringAgentSkillsSource`** — viable (measured: it needs no script runner
  and composes with the source-taking `AgentSkillsProvider` ctor), and rejected only on
  balance. `SkillCatalog` has to exist regardless — `GET /api/tools` needs every skill's name
  and description for the picker, which a predicate cannot supply — and once it exists, the
  selected `Path`s are already in hand and reuse the ctor the call site was **already** using.
  The predicate route would also still need an explicit "is anything selected" count for the
  drop-the-provider rule, since a filter that matches nothing still yields a live provider. It
  is a genuine alternative, not an impossible one; if skill *discovery* ever needs to move off
  disk, revisit this.
- **Migrate stored selections** (rewrite `skills` → per-skill keys) — rejected: the wildcard
  preserves every existing selection with no migration, same discipline as ADR 0026.
- **Title-case the picker label** ("Web search" for `websearch`) — rejected: a display-only
  transform that drifts from the key. The label is the frontmatter `name` **verbatim**, so a
  user reading the picker, the stored key and this ADR all see the same token. Adding a
  `title:` frontmatter field was also rejected — a second name to keep in sync, for two
  built-in skills.
- **Couple a skill to the tools it documents** (auto-hide `websearch` when `web_search` is
  off) — rejected: it needs a skill→tool-keys mapping the `SKILL.md` files do not carry, i.e.
  a second source of truth. A skill is guidance text; leaving it on while its tools are off is
  mildly wasteful and never an error.
- **User-authored / uploaded skills** — deferred to their own ADR: they need storage,
  ingestion, a management UI and a security position on `run_skill_script`.

## Consequences
- **Enables** per-skill selection in all three ADR 0021 layers with no new state field, no new
  grain, no new Cosmos surface, no endpoint and no migration.
- **Amends ADR 0021** twice — the skills gate is now "at least one selected skill path", not
  `enabled.Contains(skills)`; and `ToolCatalog` is no longer the *complete* static single
  source of truth. The static keys are still authoritative and centralised there.
- **A future shipped skill is silently OFF for anyone who ever pressed Save.** Because the
  picker materialises "all" as the **full explicit list**, a Save after this ships persists
  `skill:websearch, skill:scheduledprompt`. A third skill added in a later release therefore
  reaches only users, agents and conversations whose selection is still `null`. `BindAgentAsync`
  still snapshots the **bare wildcard** (`Sanitize(binding.EnabledTools ?? AllSelectableToolKeys)`),
  so that path *does* pick up future skills — **the two paths diverge**. ADR 0026 has the same
  shape for MCP servers, but there a new server is a user action; here it is a deploy-time
  change nobody consented to. Not a blocker — adding a skill is ours to sequence, and the fix is
  a one-line note in that release — but it must be written down.
- **A skill whose frontmatter `name` disagrees with its directory name is silently invisible**
  (measured, above): it will not appear in the picker and cannot be selected.
- **Nested skill directories are supported** (a `category` directory holding skill directories): the source
  recurses and `AgentFileSkill.Path` is the skill's own directory, so each nests correctly in
  the gate. The shipped layout is flat; the nested case is pinned by a test so a future
  reorganisation does not silently collapse two skills onto one key.
- **The de-dupe tie-break is ordered on `Path`, not just `Name`, and that is load-bearing.** The input
  order is the *filesystem's*, and NTFS and ext4 enumerate differently — so "first wins" on `Name` alone
  picks a different winner per platform. This shipped green on a Windows dev machine and went **red on
  Linux CI**; the fix pins the winner to the lowest path, ordinal, everywhere. Any future "first wins"
  rule over directory enumeration needs the same treatment.
- **`SkillCatalog.Discover` never throws**, and that is load-bearing rather than merely tidy:
  `Skills` is a `Lazy<T>`, which **caches a thrown exception**, so a single throw would rethrow
  on every subsequent touch and fail `BuildAgent` for every conversation on the silo for the
  life of the process. The whole body is guarded, root enumeration included.
- **Egress unchanged** (ADR 0013): per-skill selection only narrows what a conversation can
  reach; it never grants beyond model capability.

## Verification
Automated: `SkillCatalogTests` (discovery + tolerance), `ToolCatalogTests` (wildcard,
`Sanitize` ordering, key helpers), `AgentGrainTests` (per-skill gating, wildcard expansion,
legacy selection, and the two updated `null == all` guards), `ToolSelectionApiTests` (catalog
shape + label), `SkillPickerAssetsTests` (source-text guards, incl. the three-way `cb.checked`
OR — the one line nothing else in the repo catches).

**AC12 has no runtime proof** — this repo has no JS runner by design — so the frontend is
source-text-guarded plus a manual pass. Unlike ADR 0043, that manual pass **was actually run**,
in headless Chrome over CDP against DevHost (2026-08-24), and these are the observed values:

- Preferences → Tools rendered four built-ins, then a `Skills` heading, then
  `skill:scheduledprompt [scheduledprompt]` and `skill:websearch [websearch]`, all checked
  (the expanded `null == all`).
- Unchecking `skill:websearch` and pressing Save persisted
  `["web_search","location","scheduling","code_interpreter","skill:scheduledprompt"]` —
  which is also the direct observation behind the "future skill is silently off" consequence
  above.
- Reopening Preferences showed `skill:websearch = false` — it stayed unchecked.
- The composer Tools popover rendered the same grouping from the same function.

The `SkillPickerAssetsTests` three-way-OR guard was itself checked for vacuity: deleting the
`mcpWildcard` term from `cb.checked` fails it, and restoring the term makes it pass again.
