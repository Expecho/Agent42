---
id: 0049
date: 2026-08-25
status: accepted
tags: [skills, tools, storage, security, prompt-injection, no-migration, per-user]
amends: [0021, 0048]   # 0048 (per-deployment → per-user, and the "no ordering hazard" clause), 0021 (the skills gate's catalog)
amended_by: [0085, 0126, 0146]
---

# Title: Upload your own agent skills (`.md` or `.zip`)

> **Amended by [ADR 0085](0085-copilot-agent-kind.md) (2026-10-05): in a copilot chat, uploaded skills are loaded by
> Copilot natively** (`SkillDirectories`), not by `AgentSkillsProvider`, so `TruncatingSkillsProvider`'s body bound does
> not apply there; resource files are read through a path-scoped, size-capped `read_skill_resource`.

> **Amends [ADR 0048](0048-per-skill-selectable-tools.md)**, in the style of
> [ADR 0026](0026-per-mcp-server-selectable-tools.md)/[0042](0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md)/[0044](0044-persistent-topbar-action-cluster.md)/[0047](0047-native-share-for-read-only-links.md).
> Two of 0048's statements are re-scoped, not overturned: the skills portion of `GET /api/tools`
> is no longer **per-deployment** (it is now shipped ⊕ per-user overlay), and 0048's claim that
> the skills wildcard expansion "needs **no grain field** … there is no `OnActivateAsync` ordering
> hazard here" is **inverted** — that held only while skills were process-wide. ADR 0048 and ADR
> 0021 both stay `status: accepted`. **No state migration**: uploaded skills are ordinary
> `skill:{name}` keys and the `skills` wildcard keeps meaning "every skill".

## Context

ADR 0048 made each shipped skill individually selectable and explicitly deferred this to its own
ADR — "**User-authored / uploaded skills** — deferred to their own ADR: they need storage,
ingestion, a management UI and a security position on `run_skill_script`". This pays that debt.

A user wants to teach Agent 42 a procedure once and have it apply thereafter, without a deploy.
That is exactly what a skill is; the only thing missing was a way to get one in.

### What the framework decides for us

Six facts about the pinned `Microsoft.Agents.AI@1.13.0`, all **measured** with a throwaway
`dotnet run app.cs` outside the repo tree. Four of them **corrected the plan** this ADR was built
from, which is recorded here rather than quietly fixed, because a `.agentic/` claim is only
load-bearing if it is falsifiable.

1. **Skills are loaded from directory paths on disk.** `AgentSkillsProvider` is constructed from a
   list of directories and `AgentFileSkillsSource` walks them. There *is* an in-memory path
   (`AgentSkillsProvider(IEnumerable<AgentSkill>)` + `AgentInlineSkill`) that would avoid disk
   entirely — rejected, because an inline skill carries no bundled resource files. So uploaded
   skills must reach disk.

2. **The skill-directory search is hard-capped at depth 2, and the option that looks like it
   raises that does not.** `AgentFileSkillsSource.DefaultSearchDepth = 2` and
   `MaxSkillDirectorySearchDepth = 2`; `AgentFileSkillsSourceOptions.SearchDepth` governs the
   **resource/script** scan depth only (`SearchDepth = 3` and `= 9` were measured *identical* to
   the default for skill discovery, and `0` throws `ArgumentOutOfRangeException`). So a skill under
   **two** wrapper directories — a zipped parent folder, or a macOS-produced archive — yields
   **nothing at all**. *The plan assumed one `Discover(stagingRoot)` call could handle any upload;
   it cannot.* `SkillPackage` therefore walks the staging tree for `SKILL.md` itself and points
   `Discover` **at each skill's own directory**, which is measured to work and is the same
   per-skill mechanism ADR 0048 already relies on.

3. **Resource/script classification is by EXTENSION at any folder — folder names are irrelevant.**
   Resources: `.md .json .yaml .yml .csv .xml .txt`. Scripts: `.py .js .sh .ps1 .cs .csx`.
   Everything else is dropped by the parser. Measured: a `.py` file under a skill's `references/` folder is a **SCRIPT** and
   an `.md` file under an arbitrary `docs/` folder is a **RESOURCE** — the `scripts/`, `references/` and `assets/` folder literals
   in the assembly carry no meaning to the classifier. *The plan assumed folder scoping;* a filter
   keyed on the `scripts/` folder would have let a `.py` under `references/` straight through,
   which for a security control is the whole ballgame. Resource scanning reaches the skill root
   plus **one** subdirectory level by default: a file at the skill root and one a single folder below it
   are found; one two folders below it is not.

4. **`run_skill_script` is advertised UNCONDITIONALLY** — measured on a skill with no scripts and
   with no runner supplied. The plan deliberately refused to assert this either way and had the
   spike settle it; the answer vindicates the plan's original reading over the reviewing critic's.
   It stays an `ApprovalRequiredAIFunction` (we do not set `DisableRunSkillScriptApproval`) and,
   with no `AgentFileSkillScriptRunner` registered, invoking it throws
   `InvalidOperationException: … no AgentFileSkillScriptRunner was provided`.

5. **`DisableReadSkillResourceApproval = true` genuinely unlocks resources.** Measured: the tool
   changes from `ApprovalRequiredAIFunction` to a plain `ReflectionAIFunction` and returns raw
   file content. Its arguments are `{skillName, resourceName}`, and `resourceName` must be the
   **exact listed name including its folder prefix** — the `references/` prefix is part of the name, and
   the bare filename alone is not found.
   A traversal name returns "not found" — it is a lookup in a pre-discovered list, not a path join —
   and `SKILL.md` itself is not a resource.

6. **`AgentSkillFrontmatter.ValidateName` is public and strict**: `^[a-z0-9]([a-z0-9]*-[a-z0-9])*[a-z0-9]*$`,
   max 64 characters. It rejects `..`, `/`, `\`, `:`, `.`, spaces and uppercase — but it **accepts
   `con`, `aux` and `com1`**, which are Windows reserved device names.

## Decision

A signed-in user uploads a standalone `SKILL.md`, or a `.zip` containing one or more skill
directories with bundled resources, from **Preferences → Skills**. Uploaded skills merge into the
ADR 0048 picker as ordinary `skill:{name}` keys and reach the model through the same
`AgentSkillsProvider`.

**Settled by the user at interview time, not re-litigated here:** the payload is a full skill
directory with resources (not SKILL.md text alone); ownership is a **per-user overlay** (not
deployment-wide); an upload named after a shipped skill **wins and shadows it**;
`read_skill_resource` is **auto-approved** while scripts stay blocked; and a non-conforming zip
entry is **silently dropped, the rest kept, and the drop reported**.

### Shape

- **One normalized zip per skill**, whose single top-level directory is the frontmatter `name`. A
  bare `.md` is wrapped into that same shape, so there is one storage format, one materialization
  path and one test surface. `SkillPackage` is the normalizer.
- **A fresh skill id (GUID) on every upload, including a replace.** The materialized directory
  `{temp}/agent42-skills/{skillId}/{name}/` is then **immutable and needs no cache invalidation**;
  the endpoint deletes the superseded blob and directory. The picker key stays `skill:{name}`, so a
  user's selection survives a re-upload.
- **Storage**: one blob per skill at `{userId}/{skillId}.zip` in a new **`skill-packages`**
  container (`infra/modules/storage.bicep`). The Storage Blob Data Contributor grant is
  account-scoped, so this needs **no new role assignment and no manual RBAC step** (hard rule 5 is
  not engaged). The index rides `agent-state` on a new `"user-skills"` state — **no new Cosmos
  container** (the `UserPushGrain`/`UserMcpConfigGrain` pattern).
- **The container filesystem is a CACHE, not the record.** Blob storage is durable; a restart wipes
  the materialized tree and the next activation re-materializes. It is `Path.GetTempPath()` and
  never next to the shipped skills, because `src/Agent42.Backend/Dockerfile` ends with
  `USER $APP_UID` so `/app` is not writable — and because `SkillCatalog`'s process-wide `Lazy`
  cache would otherwise serve one user's uploads to everyone.
- **The framework is the authority on what a valid skill is.** Everything staged is handed to
  `SkillCatalog.Discover`, so the name/description and the "frontmatter name must equal the
  directory name" rule come from the same parser that loads the skill later. **The one exception is
  a security boundary**: a bare `.md` has no directory yet, so that path — and only that path —
  scans the `name:` line itself, and validates it **before any filesystem write**. Writing
  `{staging}/{name}/SKILL.md` first would make `name: ../../../../foo` a path-traversal write and,
  on Windows, `name: foo:bar` an alternate-data-stream write, before any validator ran.
- **Registration**: `IUserSkillProvider` is a **grain dependency**, so it is mandatory in every
  silo that activates `AgentGrain` (the `IUserMcpToolProvider`/`IPushNotificationSender` shape).
  `ISkillPackageStore` is an **endpoint** dependency, resolved **optionally via `IServiceProvider`**
  — the documented lighter pattern (`AgentApi.Speech.cs`, ADR 0029's config-health endpoint), so it
  adds no new mandatory both-hosts registration.

### Security position

This is the core content of this ADR.

- **`DisableReadSkillResourceApproval = true` is a deliberate change**, not a tidy-up. Before it,
  `read_skill_resource` waited on an approval this grain has no loop for, so bundled files would be
  stored and never readable. It applies to shipped skills too; they carry no resources, so nothing
  changes for them.
- **Scripts are blocked twice, independently.** `SkillPackage` drops every script-extension entry
  (by extension, at any depth — see measurement 3) and reports it; and the provider is built with
  `AgentFileSkillsSourceOptions { AllowedScriptExtensions = [] }`, so the **framework itself**
  refuses to discover a script even if one somehow reached disk, leaving `<available_scripts/>`
  empty. `run_skill_script` is still advertised (measurement 4) but is approval-wrapped with no
  approval loop and has no runner behind it. `DisableRunSkillScriptApproval` stays **false**.
- **Shadowing can REMOVE a control, not just add one.** The user chose "uploaded skill wins". The
  shipped `websearch` SKILL.md is the **only** place the instruction lives that `.agentic/gotchas.md`
  and CLAUDE.md's ADR 0012 bullet both describe as *mitigating* the ADR 0011 exfiltration channel —
  "it is mitigation, not prevention", and for the user's street address "the `websearch` skill only
  *mitigates* that". Uploading a skill named `websearch` replaces that text wholesale. This is a
  direct consequence of the chosen decision, so it is an **accepted residual**: the ADR names it,
  the upload response warns, and a test proves the warning fires. The alternative — reserving
  shipped names — was rejected as a reversal of the user's decision.
- **Headline consequence.** An uploaded SKILL.md is **untrusted instruction text entering the system
  prompt** (name + description always; body on `load_skill`), and its resources enter on
  `read_skill_resource`. That is a new **inbound prompt-injection channel** that can drive the ADR
  0011/0027 exfiltration channel (`web_search` queries are not allowlisted; `fetch_web_page`'s
  followed links are allowlist-exempt) and can reach a third-party model (ADR 0013). Mitigations —
  not a closure: the uploader is a signed-in allowlisted user acting on their own overlay, skills
  are individually selectable, and scripts cannot run.
- **`ZipArchive` does not create symlinks**, so `AgentFileSkillsSource.HasSymlinkInPath` is not a
  concern for extracted content.
- The endpoints sit **behind Easy Auth** like every other `/api/*` write and are **not** on
  `excludedPaths` — ADR 0042's list stays exactly `/api/agent-run`, `/api/share/*`, `/shared`,
  `/shared/*`.

### The tool-return budget

`load_skill` returns the whole SKILL.md body and `read_skill_resource` the whole resource file, and
a tool's `FunctionResultContent` is **persisted** into the session `AgentGrain` re-serializes into
one 2 MB-capped Cosmos item every turn (ADR 0004). Measured: a 300 KB SKILL.md makes `load_skill`
return **300,087 characters**; a 200 KB resource makes `read_skill_resource` return all **200,000**.
Unbounded, that sits in history forever, is re-sent every turn, and eventually makes
`WriteStateAsync` fail *after* the model has already answered — bricking the conversation, in
production only. This did not bite before because the shipped SKILL.md files are small, carry no
resources, and `read_skill_resource` was unreachable.

`SkillToolLimits` therefore caps `load_skill` at 16 KB and `read_skill_resource` at 8 KB (the
`WebToolLimits.MaxPageTextLength` order of magnitude), enforced by `TruncatingSkillsProvider` —
which wraps each tool with `DelegatingAIFunction`, preserving name and JSON schema exactly as
`TruncatingMcpTool` does for MCP results. It also caps the upload side: 500 zip entries, 2 MB per
entry (against bytes **actually copied**, never `ZipArchiveEntry.Length`), 20 MB total expansion,
and 25 installed skills per user.

### Upload auto-enables into a materialised default

ADR 0048 records that "a future shipped skill is silently OFF for anyone who ever pressed Save",
because the picker materialises "all" as the full explicit list. An uploaded skill **is** a
later-added skill — so left alone, the headline user story would do nothing for exactly the users
who have expressed preferences. On a successful upload, when the user's stored default
`EnabledTools` is **non-null**, the new `skill:{name}` key is appended to it. A `null` selection is
left null (it already means all).

Bounded and honest about what it does not fix: per-conversation overrides and per-agent snapshots
(ADR 0003/0021) are **not** rewritten — an existing conversation with a materialised override, and
an existing agent, still need a manual tick. The Skills panel note says so.

## Alternatives considered

- **In-memory inline skills** (`AgentInlineSkill`, no disk at all). Rejected by the payload
  decision: an inline skill carries no bundled resource files.
- **Deployment-wide skills** (upload once, everyone gets it). Rejected: this is a personal app with
  a per-user overlay for providers (0018) and MCP servers (0024); skills follow the same shape, and
  a shared skill is a shared prompt-injection surface.
- **Reserve shipped skill names** so an upload cannot shadow `websearch`. Rejected — it reverses
  the user's explicit "uploaded skill wins" decision. Recorded as an accepted residual instead.
- **Reuse the `attachments` container** with a synthetic owner key. Rejected: skill bundles have a
  different lifecycle (not conversation-scoped, deleted with the skill) and must never be handed a
  SAS URL, which is precisely what `IAttachmentStore.GetReadUrlAsync` exists to do.
- **Put the code in the existing `Tools/` folders** (beside `SkillCatalog.cs`), avoiding the ADR
  0039/0040 folder-list churn. Rejected: those guard lists exist so that adding a feature forces the
  docs to be updated; routing around them to save three edits inverts the mechanism.
- **`AgentSkillsProviderBuilder.UseFilter`.** Still unusable, as ADR 0048 found — re-confirmed
  here: `Build()` throws "File-based skill sources require a script runner".
- **An eviction scheme for the materialized temp tree.** Rejected as out of scope; see Consequences.

## Consequences

- **Enables** user-authored skills with bundled resources, per-user, with no migration and no new
  Cosmos container.
- **Amends ADR 0048** twice: the skills portion of `GET /api/tools` becomes per-user (shipped ⊕
  overlay), and `ExpandSkillsWildcard` **stops being `static`** — it now reads a grain field.
- **The `OnActivateAsync` ordering hazard now applies to skills.** `_userSkills` must be resolved
  **before the first `BuildAgent`**, alongside `_chatClientFactory`, `_userDefaultEnabledTools` and
  `_mcpServers`. Get it wrong and the first chat turn *and every reminder turn on that activation*
  silently ignore uploaded skills. ADR 0048's clause (a) said the opposite and is corrected in
  `.agentic/gotchas.md`.
- **A just-uploaded skill is invisible to an already-active chat grain until it reactivates** —
  identical to `_mcpServers` and accepted for the same reason. The Skills panel says "Changes take
  effect on your next new chat — no restart".
- **The materialized temp tree is unbounded until the container restarts.** Deletes and replaces
  clean up their own directory best-effort; beyond that nothing evicts. Single-replica ACA and a
  25-skill-per-user cap bound it in practice.
- **`skill:{name}` keys can go stale.** Deleting a skill leaves a `skill:{deletedName}` in any
  materialised selection. It is inert (it gates nothing) and self-heals on the next picker Save —
  the pre-existing `ToolCatalog.Sanitize` behaviour, now reachable by a user action.
- **Folder inventories move by one**: `src/Agent42.Agents` gains `UserSkills/` (17 folders, was 16)
  and `src/Agent42.Backend` gains `UserSkills/` (18, was 17), and `MapAgentApi` has thirteen
  endpoint groups (was twelve). Corrected in place in ADR 0039, ADR 0040 and CLAUDE.md — a folder
  list is a factual inventory, not a decision, so neither ADR is superseded.
- **The framework XML-escapes a skill description into the system prompt** (`user's` arrives as
  `user&apos;s`, `<` as `&lt;`, `&` as `&amp;`). Harmless for the model, but it makes an assertion
  on a description containing punctuation fail confusingly.
- **`con`, `aux`, `com1` and friends pass the framework's own name rule** yet cannot be directory
  names on Windows, so `SkillPackage` rejects them explicitly. Same platform-dependence class as the
  ADR 0048 de-dupe tie-break that was green on Windows and red on Linux CI.
- **The case-collision the platform-dependence argument actually closes is on ZIP ENTRY names, not on
  skill names.** Two zip entries whose paths differ only in the case of a directory segment are ONE file
  on NTFS (the second silently overwrites the first) and two on ext4, so the extraction de-dupe is
  `OrdinalIgnoreCase` and reports the loser — a real bug, pinned by
  `Normalize_DuplicateEntryNames_FirstWins`. The `OrdinalIgnoreCase`
  comparisons in `MergedSkills()`, `UserSkillsGrain.SaveAsync` and the `/api/tools` merge are
  **defence in depth against a case measurement 6 already forecloses**: the framework's name rule is
  lowercase-only, so two *valid* skills differing only in case cannot exist and an upload named
  `WebSearch` is not installable at all. They are kept because the rule lives in the framework, not in
  code this repo controls — but the ADR should not claim they close a live bug, and the plan's §9
  premise ("an upload named `WebSearch` would not shadow the shipped `websearch`") is void for the same
  reason. AC9's case-differing half is consequently unreachable through the real path and is not
  asserted; the exact-name shadowing it does cover is.
- **Egress (ADR 0013) gains a new inbound vector** — see the security position above. Nothing new
  leaves the tenant that did not before, but what the model is *told to do* is now user-supplied.

## Verification

**Automated.** `SkillPackageTests` (normalization: bare `.md`, wrapper depths 0–4, resources by
extension not folder, script/binary drops, zip-slip, per-entry and total-expansion caps, entry-count
cap, duplicate and case-colliding entry names, Windows reserved names, over-deep resources,
name≠directory, and validate-before-write for nine hostile names); `UserSkillsGrainTests`
(upsert-by-name, case-insensitive replace, superseded-row return, no-op delete, persistence, per-user
scoping); `UserSkillProviderTests` (materialize-once, version cache, corrupt/missing package degrades
without throwing or leaving a partial tree, and — the one that matters for AC3 — the framework's own
`load_skill` listing the materialized resources); `AgentGrainTests` (activation-before-first-BuildAgent,
advertisement, shadowing, wildcard expansion across both layers, zero-selected-skills still drops the
provider, truncation of both oversized returns, and no script advertised even with one planted on
disk); `UserSkillsApiTests` (CRUD, 400s, `/api/tools` merge, shadowing warning, auto-enable on a
materialised default and null left null, per-user isolation); `UserSkillPickerAssetsTests` +
`ProjectLayoutTests` + `PwaAssetsTests` (frontend source-text guards, folder set, endpoint-group
registration, `SHELL` ≡ directory, `CACHE` bump); `GrainStorageSerializerTests` +
`SerializationCompatibilityTests` (the new persisted state's Cosmos `$type` and field-for-field
round-trip). Full suite: **817 passed, 0 failed**.

**Not provable in-repo, and said plainly.** That `read_skill_resource` returns content *to the
model* and that `run_skill_script` is advertised unconditionally are **spike** results (measurements
4 and 5), not in-repo tests — the same honest treatment ADR 0037/0043 give their untestable halves.

**Manual.** Unlike ADR 0043 the manual leg **was** run — headless Chrome over CDP against DevHost
(2026-08-25). Observed values:

- Uploading a bare `SKILL.md` returned `{skills:[{name:"mynotes",files:["SKILL.md"]}],skipped:[],warnings:[]}`.
- A two-skill zip carrying a notes resource under `alpha`'s `references/` folder and a shell script under
  its `scripts/` folder installed both skills, listing the notes file among `alpha`'s files and reporting
  the script as skipped with "scripts are not supported and are never stored".
- `GET /api/tools` then listed `skill:alpha, skill:beta, skill:mynotes, skill:scheduledprompt,
  skill:websearch` — uploads and shipped skills, indistinguishable and sorted together.
- **The auto-enable bug this exists to catch was exercised deliberately**: after saving the picker
  (materialising `["web_search","location","scheduling","code_interpreter","skill:alpha","skill:beta",
  "skill:mynotes","skill:scheduledprompt","skill:websearch"]`), uploading a *third* skill left the
  profile as that list **plus `skill:invoices`**. A fresh user has a `null` selection and takes the
  wildcard path, which is exactly why the Save had to come first.
- Uploading a skill named `websearch` returned the warning
  `"'websearch' replaces the built-in skill of the same name. The built-in version is no longer used
  in your chats."` and the picker still showed **one** `skill:websearch` key.
- A **new** chat's effective tools (`GET /api/conversations/{id}/tools`, computed by the grain on a
  real activation over the merged catalog) contained every uploaded key.
- Deleting `beta` returned 204; both `/api/skills` and `/api/tools` dropped it, and its materialized
  directory was gone.
- The materialized tree on disk was exactly `{temp}/agent42-skills/{skillId}/{name}/…`, with `alpha`'s
  notes resource present under its `references/` folder and **no script file anywhere**.
- Preferences → Skills listed all four installed skills with their descriptions; the tablist was
  `[tabGeneral, tabTools, tabProviders, tabMcp, tabConfig, tabSkills]` with **MCP still at index 3**,
  so the ADR 0025 OAuth return's `profileTabs.select(3)` deep-link is intact.

**One thing DevHost cannot show, and it is not claimed.** Its canned chat client ignores the system
prompt entirely, so the `[sys:…]` echo that proves advertisement to the model does not exist offline.
That claim rests on `AgentGrainTests` over `FakeChatClient`, not on this manual pass.
