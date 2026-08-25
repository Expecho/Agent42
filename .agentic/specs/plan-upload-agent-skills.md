# Plan — Upload agent skills (.md or .zip)

planned-against: main @ cb77bd2

## Overview

A signed-in user uploads their own agent skills — a standalone `SKILL.md` file, or a zip
containing one or more skill directories with bundled reference files — from Preferences.
Uploaded skills merge into the ADR 0048 per-skill picker as ordinary `skill:{name}` keys and
reach the model through the same `AgentSkillsProvider`.

Why now: ADR 0048 (merged as #88) made skills individually selectable and explicitly deferred
this to its own ADR — "**User-authored / uploaded skills** — deferred to their own ADR: they need
storage, ingestion, a management UI and a security position on `run_skill_script`"
(`.agentic/decisions/0048-per-skill-selectable-tools.md`, Alternatives). This plan pays that debt.

## Affected areas

Verified against the tree at `cb77bd2`.

**New files**
- `src/Agent42.Agents/UserSkills/` and `src/Agent42.Backend/UserSkills/` — two new feature folders
  (ADR 0039/0040). **Not `Skills/`**: that name is already taken in the Agents project by the
  *content* folder holding the shipped SKILL.md files, which the csproj copies next to the built
  assembly — see §7.
- `src/Agent42.Agents/UserSkills/IUserSkillsGrain.cs`, `UserSkillsGrain.cs`, `UserSkillConfig.cs`
- `src/Agent42.Agents/UserSkills/ISkillPackageStore.cs`, `InMemorySkillPackageStore.cs`
- `src/Agent42.Agents/UserSkills/IUserSkillProvider.cs`
- `src/Agent42.Agents/UserSkills/SkillPackage.cs` (zip/​md normalizer)
- `src/Agent42.Agents/UserSkills/SkillToolLimits.cs` (return budget — §3)
- `src/Agent42.Backend/UserSkills/BlobSkillPackageStore.cs`, `UserSkillProvider.cs`
- `src/Agent42.Backend/UserSkills/AgentApi.UserSkills.cs`, `UserSkillContracts.cs`
- `src/Agent42.Backend/wwwroot/js/skills.js`
- `tests/Agent42.Tests/SkillPackageTests.cs`, `UserSkillsGrainTests.cs`,
  `UserSkillsApiTests.cs`, `UserSkillProviderTests.cs`, `UserSkillPickerAssetsTests.cs`
- `.agentic/decisions/0049-upload-agent-skills.md`

**Modified**
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — merged catalog, `ExpandSkillsWildcard`,
  provider options
- `src/Agent42.Agents/Conversations/AgentGrain.cs` — `_userSkills` field + activation ordering + ctor dep
- `src/Agent42.Agents/Tools/SkillCatalog.cs`, `Tools/ToolCatalog.cs` — doc claims only (§14)
- `src/Agent42.Backend/Tools/AgentApi.Tools.cs` — per-user skill descriptors + its doc claim (§14)
- `src/Agent42.Backend/Api/AgentApi.cs` — register the new endpoint partial
- `src/Agent42.Backend/Program.cs`, `tools/Agent42.DevHost/Program.cs` — registrations (§12)
- `src/Agent42.Backend/wwwroot/index.html` — Skills tab + panel + dialog markup
- `src/Agent42.Backend/wwwroot/js/dom.js` — tab pair + new element exports
- `src/Agent42.Backend/wwwroot/js/main.js` — `wireSkills()`
- `src/Agent42.Backend/wwwroot/sw.js` — SHELL entry + CACHE bump
- `src/Agent42.Backend/wwwroot/styles.css` — reuse `.providers__*` classes; additions only if needed
- `infra/modules/storage.bicep` — one new container
- Tests: `ProjectLayoutTests.cs` (**three** by-name lists — §13),
  `GrainStorageSerializerTests.cs` and `SerializationCompatibilityTests.cs` (§13),
  `AgentGrainTests.cs`, `ToolCatalogTests.cs`, and the five silo configurators in §12
- `CLAUDE.md`, `.agentic/gotchas.md`, and the folder inventories in ADR 0039 / ADR 0040 (§13)

`src/Agent42.AppHost/AppHost.cs` is **not** affected: it declares `AddBlobs("blobs")` with no
per-container list, so the emulator container is created at runtime by the store itself (§ Step 3).

## Context & constraints

Read before writing code: `.agentic/gotchas.md` — "An agent tool's RETURN value is persisted…"
(~line 132), "`AgentSkillsProvider` is a TOOL…" (~line 500), the `null == all` entry (~line 1210,
esp. the ADR 0048 paragraph). ADRs: 0048 (extended and **amended** by this work), 0024/0026 (the
overlay shape being copied), 0021 (the selection layers), 0018 (per-user overlay philosophy), 0004
(both halves — bytes out of Cosmos *and* the 2 MB session cap), 0013 (egress), 0011/0027 (the
exfiltration channel this feature feeds).

This plan was adversarially reviewed before approval; see the **Review** section at the end for
what was changed and what was deliberately not.

### 1. User decisions from the interview (settled — do not re-litigate)

| Decision | Chosen | Rejected |
|---|---|---|
| Payload | **Full skill directory with bundled resources** | SKILL.md-text-only (in-memory inline skills) |
| Ownership | **Per-user overlay** | Deployment-wide |
| Name clash with a shipped skill | **Uploaded skill wins — it shadows the built-in** | Reject upload; namespace uploads separately |
| `read_skill_resource` | **Auto-approve it**; scripts stay blocked | Leave approval on; split shipped/uploaded |
| Non-conforming zip entries | **Silently drop, keep the rest, report what was skipped** | Reject the whole upload; store everything |

Two consequences of these were surfaced by review and resolved as **plan decisions**, not user
questions — §5 (upload auto-enables) and §11 (shadowing can remove a control). Both are called out
to the user at approval time; if either is unwanted, that changes the plan.

### 2. The framework decides the storage shape, and it reads from disk

`AgentSkillsProvider` is constructed from a list of **directory paths** and
`AgentFileSkillsSource` walks them. There is an in-memory path in the pinned package
(`AgentSkillsProvider(IEnumerable<AgentSkill>)` + `AgentInlineSkill`) that would avoid disk
entirely — **rejected by the payload decision above**, because an inline skill carries no bundled
resources. So uploaded skills must reach disk.

Consequence: durable bytes live in blob storage; the container filesystem is a **cache**, not the
record. ACA runs this app single-replica (`infra/main.bicep` sets `minReplicas = maxReplicas = 1`
because Orleans uses localhost clustering), so there is no cross-replica coherence problem, but a
restart still wipes the cache.

**Materialize into `Path.GetTempPath()`, never next to the shipped skills.**
`src/Agent42.Backend/Dockerfile` ends with `USER $APP_UID`, so the app runs non-root and `/app`
(hence `AppContext.BaseDirectory/Skills`, where `SkillCatalog` reads) is **not writable**. Writing
uploaded skills there would fail in production while working on a dev box — and would also make
`SkillCatalog`'s process-wide `Lazy` cache serve one user's uploads to everyone. Uploaded skills
are resolved per-user through `IUserSkillProvider`; `SkillCatalog` stays read-only and shipped-only.

### 3. The tool return budget — the trap this feature walks straight into

`.agentic/gotchas.md` (~line 132): a tool's `FunctionResultContent` is **persisted** into the
session that `AgentGrain` re-serializes into a single 2 MB-capped Cosmos item every turn. An
unbudgeted return "would sit in history forever, be re-sent to the model every turn, and eventually
make `WriteStateAsync` fail *after* the model had already answered — bricking the conversation, in
production only". The gotcha's rule is explicit: **any new tool must count its return payload
against this budget.**

This feature opens exactly such a path. `load_skill` returns the full SKILL.md body, and §11's
`DisableReadSkillResourceApproval = true` promotes `read_skill_resource` from unreachable to live —
returning **user-uploaded file content**, from a bundle capped only at the 10 MB upload limit.
Neither is bounded today, and the failure is invisible to every test in this plan because the
offline document/attachment stores have no 2 MB limit.

**Add `src/Agent42.Agents/UserSkills/SkillToolLimits.cs`**, modelled on
`src/Agent42.Agents/Web/WebToolLimits.cs` (copy its doc-comment discipline — that file explains
*why* the numbers exist, which is the part that must not be lost). Cap the returned skill body and
the returned resource text, with a `TruncationMarker`. Enforce it by wrapping the provider's tools
the way `UserMcpToolProvider` truncation-wraps MCP tool results (ADR 0024's "a tool's return value
is persisted … so each is truncation-wrapped") — that is the existing exemplar for wrapping tools
you do not own. Pick the constants to match `WebToolLimits.MaxPageTextLength`'s order of magnitude
unless Step 1 measures a reason not to.

### 4. Why one normalized zip per skill, content-addressed by a fresh id

Store exactly one blob per installed skill: a **normalized zip** whose single top-level directory
is the skill's frontmatter `name`. A bare `.md` upload is wrapped into that same shape, so there is
one storage format, one materialization path, and one test surface.

Mint a **fresh skill id (GUID) on every upload**, including a replace. The materialized directory
`{temp}/agent42-skills/{skillId}/{name}/` is then immutable and needs **no cache invalidation** — a
replaced skill gets a new id and a new directory, and the old blob is deleted by the endpoint. The
picker key stays `skill:{name}`, so a user's selection survives a re-upload.

Cleanup: `DELETE /api/skills/{id}` removes the blob, the grain row, **and** best-effort the
materialized directory (a failure to delete must not fail the request). A *replaced* skill's old
directory is likewise removed best-effort. Beyond that, rely on container restarts — do not build
an eviction scheme, and record the unbounded-until-restart property in the ADR rather than leaving
it unexamined.

### 5. An uploaded skill is OFF by default for exactly the users who matter — decide it, don't inherit it

`.agentic/decisions/0048-per-skill-selectable-tools.md`: "**A future shipped skill is silently OFF
for anyone who ever pressed Save.** Because the picker materialises 'all' as the **full explicit
list** … a third skill added in a later release therefore reaches only users, agents and
conversations whose selection is still `null`."

An uploaded skill **is** a later-added skill. Left alone, the headline user story — upload a skill,
use it — silently does nothing for any user who has ever opened the Tools picker and pressed Save.
And the bug is invisible to Step 10, because a fresh DevHost user has a `null` selection and takes
the wildcard path.

**Plan decision:** on a successful upload, if the user's stored default `EnabledTools` is
**non-null**, append the new `skill:{name}` key to it. Uploading a skill is an explicit act of
wanting it; a non-null list means the user has expressed preferences, and the new skill was not one
of them because it did not exist. A `null` selection is left `null` (it already means all).

Bounded and honest about what it does *not* fix: per-conversation overrides and per-agent snapshots
(ADR 0003/0021) are **not** rewritten — an existing conversation with a materialised override, and
an existing agent, still need a manual tick. Say so in the ADR and in the UI note.

### 6. Do not hand-parse SKILL.md frontmatter — reuse `SkillCatalog.Discover`

`SkillCatalog.Discover(root)` already runs the framework's own parser, already tolerates malformed
input by returning a shorter list, and already returns exactly `(Name, Description, Path)`. Extract
the upload to a staging directory and call `Discover(stagingRoot)`. That makes the framework the
single authority on what a valid skill is, and means the "frontmatter `name` must equal the
directory name" rule (gotchas, ~line 523) is enforced by the same code that will later load the
skill — not by a second parser that can drift.

**The one exception, and its ordering is a security boundary.** A bare `.md` upload has no
directory yet, so that path — and only that path — scans the `name:` frontmatter line to choose a
directory name.

**Validate that scanned name against the framework's own name rule BEFORE it touches the
filesystem.** `AgentSkillFrontmatter` exposes `MaxNameLength` and the framework enforces a
restrictive character set; the scanned value must be checked against an equally restrictive
allowlist (lowercase letters, digits, hyphen; length-capped) and rejected with a 400 otherwise.
Writing `{staging}/{name}/SKILL.md` first and validating afterwards — which is what an earlier
draft of this plan said — makes `name: ../../../../foo` a path-traversal write, and on Windows
`name: foo:bar` an alternate-data-stream write, **before** any validator runs. The same unvalidated
value would then become the normalized zip's top-level directory (§4) and part of the
materialization path.

The same allowlist applies to every skill name that reaches a path, whatever its source.

### 7. Folder-name collision hazard specific to this repo

`src/Agent42.Agents/Skills/` is a **content** folder — the csproj has
`<Content Include="Skills\**\*">` copying it next to the built assembly, which is exactly where
`SkillCatalog` reads from at runtime. Putting new `.cs` files under `Skills/` would ship source
files into the deployed `Skills/` directory and hand them to `AgentFileSkillsSource`. Use
`UserSkills/`. Namespaces stay flat `Agent42.Agents` (ADR 0039 — folders and namespaces
deliberately disagree; a persisted type with a folder-shaped namespace makes every Cosmos document
unreadable).

Placing this code in the existing `Tools/` folders instead (beside `SkillCatalog.cs`) was
considered — it would avoid the §13 folder-list churn entirely. Rejected: this is a storage +
grain + provider + endpoint feature, and ADR 0039/0040's whole point is that a feature gets a
folder. The churn is the mechanism working, not a cost to dodge.

### 8. Storage placement

The Storage Blob Data Contributor grant is **account-scoped**
(`infra/modules/storage-role-assignment.bicep`, `scope: account`), so a new container needs a Bicep
resource and **no new role assignment and no manual RBAC step** (hard rule 5 is not engaged). Add a
`skill-packages` container to `infra/modules/storage.bicep` beside `attachments` and
`dataprotection`.

Reusing the `attachments` container with a synthetic owner key was considered and rejected: skill
bundles have a different lifecycle (not conversation-scoped, deleted with the skill), must never be
handed a SAS URL, and `IAttachmentStore`'s `GetReadUrlAsync` exists to do exactly that.

Grain state rides the existing `agent-state` provider on a new state name `"user-skills"` — **no
new Cosmos container** (the `UserPushGrain`/`UserMcpConfigGrain` pattern).

### 9. Casing, collisions and the platform-dependence class of bug this repo has already been bitten by

`SkillCatalog` de-dupes with `StringComparer.Ordinal`, so today `WebSearch` and `websearch` are two
different skills. Safe while skills ship from one tree; **not** safe once a user uploads one:

- Shadowing is defined by name equality. Under ordinal comparison an upload named `WebSearch` would
  not shadow the shipped `websearch` — the picker would show two boxes whose labels differ only in
  case, which reads as a duplicate and silently splits the selection.
- Directory names differ by case only on some filesystems. NTFS says `WebSearch` and `websearch`
  are one directory; ext4 says two. This is precisely the bug class that ADR 0048's own follow-up
  commit (`30cd8e5`, "make the skill de-dupe tie-break platform-independent") fixed — green on
  Windows, red on Linux.

**Decision:** the merge/shadow comparison is `StringComparer.OrdinalIgnoreCase`; the key and label
emitted are the surviving entry's name **verbatim** (the upload's, since the upload wins). Within a
single zip, two skill directories differing only in case are a **collision** — keep the first by
ordinal path and report the other as skipped, mirroring `SkillCatalog`'s existing tie-break so the
winner is the same on both platforms. Do not change `SkillCatalog`'s internal ordinal de-dupe; that
is separate, already-pinned behaviour.

### 10. Zip guards — the full set

All required, none optional. The normalizer must:

- Reject entries whose normalized full path escapes the staging root (**zip-slip**), and reject
  rooted/drive-qualified entry names.
- Cap entry count, per-entry uncompressed size, and **total** uncompressed size. `ZipArchiveEntry.Length`
  is attacker-controlled metadata — cap the bytes **actually copied**, not the declared length
  (**zip bomb**).
- Handle **duplicate entry names** deterministically (first wins, rest reported) rather than
  letting `ExtractToFile` throw or silently last-win.
- Reject Windows-hostile names — reserved device names (`CON`, `AUX`, `NUL`, `COM1`…), trailing
  dots/spaces, and `:` — since the same tree is created on both Windows (dev/CI) and Linux (prod).
- Drop entries outside the allowed extension set, and outside the folders the framework actually
  reads (§ Step 1 measures both — see N1 rationale below).
- Cap the **number of installed skills per user**, so per-user disk is bounded by more than the
  10 MB per-upload limit.

`ZipArchive` does not create symlinks, so `AgentFileSkillsSource.HasSymlinkInPath` is not a concern
for extracted content — state that in the ADR rather than leaving it unexamined.

**Resource discovery is folder- and extension-scoped by the framework.** The assembly carries
`references`, `assets` and `scripts` folder literals and an extension allowlist including `.md`,
`.yaml`, `.csv` (verified in `Microsoft.Agents.AI.dll`, 1.13.0). So a normalizer allowlist that
disagrees with the framework's stores bytes the model can never read. Step 1 measures the real set;
the normalizer's allowlist must be derived from that measurement, not guessed.

### 11. Security position (this is the ADR's core content)

- **`DisableReadSkillResourceApproval = true` is a deliberate change**, not a tidy-up. Today only
  `DisableLoadSkillApproval` is set, so `read_skill_resource` waits on an approval this grain has no
  loop for — bundled files would be stored and never readable. Setting it makes the resources half
  of the feature work. It applies to shipped skills too; they carry no resources, so no behaviour
  change there, but the ADR must say so. It also creates the return path §3 budgets.
- **Scripts.** The normalizer drops script entries and no `AgentFileSkillScriptRunner` is
  registered. **Do not assert in the ADR whether `run_skill_script` is still advertised** — Step 1
  measures it. Both readings were argued during review and neither could be confirmed from the
  pinned assembly. What the ADR states must be the measured value, plus the corollary either way:
  if a script entry ever leaks past the normalizer, the tool may become advertised with no runner
  behind it and no approval loop — so the normalizer's script filter is a security control, not
  hygiene. `DisableRunSkillScriptApproval` stays **false**.
- **Shadowing can remove a control, not just add one.** The user chose "uploaded skill wins". The
  shipped `websearch` SKILL.md is the **only** place the instruction lives that
  `.agentic/gotchas.md` (~line 281) and CLAUDE.md's ADR 0012 bullet both describe as *mitigating*
  the ADR 0011 exfiltration channel — "it is mitigation, not prevention", and for the user's street
  address "the `websearch` skill only *mitigates* that". Uploading a skill named `websearch`
  replaces that text wholesale. This is a direct consequence of the chosen decision, so it is
  **recorded as an accepted residual** rather than blocked: the ADR must name it, the upload
  response must warn when a shipped skill is being shadowed, and an AC must prove the warning
  fires. If that trade is unwanted, the fix is to reserve shipped names — a change to §1's decision.
- **Headline consequence.** An uploaded SKILL.md is untrusted instruction text entering the system
  prompt (name + description always; body on `load_skill`), and its resources enter on
  `read_skill_resource`. That is a new **inbound prompt-injection channel** that can drive the ADR
  0011/0027 exfiltration channel (`web_search` queries are not allowlisted; `fetch_web_page`'s
  followed links are allowlist-exempt) and can reach a third-party model (ADR 0013). Mitigations:
  the uploader is a signed-in allowlisted user acting on their own overlay, skills are individually
  selectable, scripts cannot run. Record it honestly; do not claim it is closed.
- The endpoints sit behind Easy Auth like every other `/api/*` write, and are **not** on
  `excludedPaths` (ADR 0042 — that list stays exactly `/api/agent-run`, `/api/share/*`, `/shared`,
  `/shared/*`).

### 12. Registration — pick the lighter pattern for the endpoint, the mandatory one for the grain

- **`IUserSkillProvider` is a grain dependency**, so it is mandatory in every silo that activates
  `AgentGrain` (the `IUserMcpToolProvider`/`IPushNotificationSender` shape). There is no way around
  that.
- **`ISkillPackageStore` is an endpoint dependency, so resolve it optionally via `IServiceProvider`**
  — the documented lighter pattern used deliberately twice already (`AgentApi.Speech.cs` and the
  ADR 0029 config-health endpoint, "adding **no new mandatory both-hosts registration**"). A
  minimal-API parameter the host cannot resolve breaks route matching for the *whole* host, which
  is why that pattern exists. Do not make it a constructor/handler parameter.

The silo sites, from `grep -rln "AddSingleton<IUserMcpToolProvider" tests/ src/ tools/`:

1. `src/Agent42.Backend/Program.cs`
2. `tools/Agent42.DevHost/Program.cs`
3. `tests/Agent42.Tests/TestSiloConfigurator.cs`
4. `tests/Agent42.Tests/NoModelSiloConfigurator.cs`
5. `tests/Agent42.Tests/AttachmentRehydrateTests.cs`
6. `tests/Agent42.Tests/CapSizedWebPageFetcher.cs` — carries its own warning that it is "the one
   configurator where a new grain dependency has to be added by hand — miss it and every activation
   on THIS silo fails"
7. `tests/Agent42.Tests/FakeGeocoder.cs`

Re-run that grep before starting — it is the authoritative list, not this table.

### 13. Five by-name guard lists that this change silently breaks

Verified at `cb77bd2`. Each is asserted **by name, not by count**, precisely so that adding
something forces the docs it mirrors to be updated. All fail the build if missed:

- **`ProjectLayoutTests.ExpectedFolders()`** (~line 89) — exact feature-folder set per project.
  Adding `UserSkills/` to Agents *and* Backend is two edits — **and to the prose they mirror**:
  `CLAUDE.md:669` says "16 top-level feature folders" (Agents) and the ADR 0040 bullet says "17
  feature folders" (Backend); ADR 0039/0040 carry the same. Update prose to match the list rather
  than trusting either number. The test's own comment ties them together.
- **`ProjectLayoutTests.AgentApiParts()`** (~line 153) — the twelve endpoint groups by path and
  map-method. `UserSkills/AgentApi.UserSkills.cs` / `MapUserSkillEndpoints` is the thirteenth. This
  is the same test that catches ADR 0040's "exists but never registered" trap, so the row is what
  makes Step 7's registration provable.
- **`ProjectLayoutTests.FrontEndWiredModules()`** (~line 225) — needs `{ "skills.js", "wireSkills" }`.
- **`GrainStorageSerializerTests.PersistedTypeNames()`** (~line 53) — twelve hard-coded literals
  guarding the Cosmos `$type`. Add `Agent42.Agents.UserSkillsState`. This is the ADR 0039
  data-at-rest guard whose omission makes documents unreadable in production; do not "improve" it
  into a `typeof(T).FullName` derivation.
- **`SerializationCompatibilityTests`** — every one of the twelve existing states has a
  `*_RoundTripsFieldForField` test. Add one for the new state.

### 14. Doc claims this change falsifies (hard rule 2 — fix in the same MR)

- `src/Agent42.Agents/Tools/ToolCatalog.cs` (~line 10) — "per-skill keys … **per-deployment, read
  from disk** via `SkillCatalog`"; and (~line 35) "Each skill **shipped** in `SkillCatalog`".
- `src/Agent42.Backend/Tools/AgentApi.Tools.cs` (~line 20) — "**PER-DEPLOYMENT, not per-user**:
  skills ship with the image … **there is no management/enable flag** to honour."
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (~line 141) — "**the skills ship no scripts
  or resources**."
- `src/Agent42.Agents/Tools/SkillCatalog.cs` class doc — it becomes the shipped/base *layer*.
  Its `Discover` doc-comment about nested skills (~line 47) must also be reconciled with what
  Step 1 measures (§ Step 1 item 4).
- The `null == all` gotcha's ADR 0048 clause **(a)** — "the expansion needs **no grain field** …
  there is no `OnActivateAsync` ordering hazard here" — is **inverted** by §15.
- `.agentic/decisions/0048-*.md` is **active**: ADR 0049 must declare that it **amends 0048**
  (per-deployment → per-user), in the style of 0026/0042/0044/0047.

### 15. This feature re-opens an ordering hazard, and adds an accepted staleness

Making skills per-user means `ExpandSkillsWildcard` and the `BuildAgent` gate must both see the
merged catalog, so `AgentGrain` needs a `_userSkills` field resolved in `OnActivateAsync` **before
the first `BuildAgent`** — the hazard already guarded for `_chatClientFactory`,
`_userDefaultEnabledTools` and `_mcpServers`. Get it wrong and the first chat turn *and every
reminder turn on that activation* silently ignore uploaded skills. `ExpandSkillsWildcard` stops
being `static`.

**Consequence, identical to `_mcpServers` and accepted for the same reason:** a just-uploaded skill
is invisible to an **already-active** chat grain until it reactivates. Mirror the existing UI note
("Changes take effect on your next new chat — no restart") and note it in Step 10's script, or the
manual leg will read as a bug.

### 16. Frontend tab index is load-bearing

`js/dom.js` documents it: `initMcpOAuthReturn` deep-links to `profileTabs.select(3)`, so MCP must
stay index 3 and Configuration 4. **Append the Skills tab after Configuration** → index 5. Anything
earlier breaks the MCP OAuth return. Already guarded by `ConfigHealthAssetsTests`.

Every MR touching `wwwroot/js/` must bump `sw.js`'s `CACHE` (`agent42-shell-v20` → `-v21`) and add
the module to `SHELL` — `SHELL` is all-or-nothing and a stale or missing entry kills the whole
service worker (`ProjectLayoutTests` enforces SHELL ≡ directory scan).

## Steps

Steps 2, 3 and 4 are **parallelizable** (disjoint files, no shared symbols). Concurrent dispatch
requires `isolation: "worktree"`.

### Step 1 — Spike the framework's actual behaviour (blocking, and broader than it looks)

`/spike-dotnet` against `Microsoft.Agents.AI 1.13.0` (the pinned version). Measure — do not reason
about — all five:

1. `new AgentSkillsProvider(paths, options: new AgentSkillsProviderOptions { DisableLoadSkillApproval = true, DisableReadSkillResourceApproval = true })`
   constructs and functions when `paths` mixes a directory under `AppContext.BaseDirectory/Skills`
   with one under `Path.GetTempPath()`, **no script runner supplied**.
2. With `DisableReadSkillResourceApproval = true`, `read_skill_resource` against a skill containing
   `SKILL.md` + `references/notes.md` **returns file content** rather than an approval request.
   Record the tool's argument shape. **If this fails, stop and report** — the resources half does
   not work and §1's payload decision needs revisiting.
3. **Which tools are advertised** for three fixtures: a skill with neither scripts nor resources; one
   with only resources; one with a `scripts/` entry. This settles whether `run_skill_script` is
   registered unconditionally or gated on content — a claim this plan deliberately does **not**
   assert (§11). The repo already has the instrument: `FakeChatClient` echoes tool schemas by name.
4. **`SkillCatalog.Discover` on an extracted tree** — the mechanism §6 delegates everything to.
   Measure `Path` for: a skill at the staging root; under one wrapper directory; under **two**
   wrapper directories; two skills side by side. `SkillCatalog.cs`'s own `Discover` doc-comment
   (~line 47) claims a nested skill is "recorded under `Skills/category`", which if true breaks §6
   outright. Settle it and reconcile the comment in the same MR either way. Also find the search
   depth limit — a double wrapper (a zipped parent folder, or a macOS-produced archive) must not
   silently yield nothing.
5. **The resource folder and extension allowlist** the framework actually honours (§10) — the
   assembly carries `references`, `assets`, `scripts` and extensions including `.md`/`.yaml`/`.csv`.
   The normalizer's allowlist is derived from this measurement.

Record measured values in the spike output; they become ADR 0049 evidence.

*Verify:* the spike's own output.

### Step 2 — `SkillPackage` normalizer *(parallelizable)*

`src/Agent42.Agents/UserSkills/SkillPackage.cs` — takes `(fileName, bytes, stagingRoot)`, returns
parsed skills plus a skipped-entry report, per §4, §6, §9 and §10. Each returned skill carries its
normalized zip bytes, name, description and resource file list.

*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~SkillPackageTests`. Cover: bare
`.md`; zip with one skill at root; one wrapper dir; **two** wrapper dirs; two skills; **name-based
path traversal in a bare `.md`'s frontmatter, rejected before any write** (§6); zip-slip entry;
oversized entry; too many entries; total-size bomb (declared length lying about actual bytes);
duplicate entry names; case-only-differing skill dirs; Windows reserved name; script/binary entries
dropped and reported; `assets/` and non-allowlisted-folder resources handled per Step 1's
measurement; zip with no valid SKILL.md → error; `SKILL.md` whose frontmatter name ≠ directory name
→ not installed.

### Step 3 — `ISkillPackageStore` + implementations + Bicep *(parallelizable)*

Interface and `InMemorySkillPackageStore` in `src/Agent42.Agents/UserSkills/`;
`BlobSkillPackageStore` in `src/Agent42.Backend/UserSkills/`, copying
`src/Agent42.Backend/Attachments/BlobAttachmentStore.cs`. Blob key `{userId}/{skillId}.zip`,
container `skill-packages`. Add the container to `infra/modules/storage.bicep`.

**Keep the `if (_container.CanGenerateSasUri) → CreateIfNotExistsAsync` block.** It reads as SAS
code but is the **Azurite/AppHost container bootstrap**: production pre-creates the container in
Bicep and uses an identity client, while the local emulator has no pre-created container and
`AppHost.cs` declares no container list. Dropping it as "SAS-related" fails the Aspire path with
`ContainerNotFound`. Everything else SAS-related (`GetReadUrlAsync`, user-delegation keys) **is**
dropped — a skill bundle must never be handed a SAS URL.

*Verify:* `dotnet build Agent42.slnx`; `az bicep build --file infra/main.bicep` (compile only, no
deployment). The in-memory store is exercised by Steps 5 and 7.

### Step 4 — `IUserSkillsGrain` + state *(parallelizable)*

`src/Agent42.Agents/UserSkills/`. Copy `UserMcpConfigGrain`'s shape exactly:
`[PersistentState("user-skills", AgentGrain.StorageProviderName)]`, `[GenerateSerializer]` +
`[Alias]` on every persisted type, sequential `[Id(n)]`, a `Version` bumped on every mutation,
`GetAsync` / `GetVersionAsync` / `SaveAsync` / `DeleteAsync`. `SaveAsync` upserts **by name**
(case-insensitively, §9) and returns the superseded row so the caller can delete its blob and
directory — the grain does no blob or disk IO.

*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~UserSkillsGrainTests` — upsert;
replace-by-name returns the superseded row; case-differing name replaces rather than duplicates;
delete; version bumps; empty default. Plus the §13 serialization rows.

### Step 5 — `IUserSkillProvider` + materialization (depends on 3, 4)

Interface in Agents, singleton `UserSkillProvider` in Backend, copying
`src/Agent42.Backend/Mcp/UserMcpToolProvider.cs`'s per-user version-cache shape. Reads the grain
version, materializes each skill's zip to `{temp}/agent42-skills/{skillId}/{name}/` if absent,
returns `SkillInfo`-shaped entries. Best-effort: a failed materialization loses that skill, never
throws, never fails grain activation.

*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~UserSkillProviderTests` — cache hit
by version; materializes once; corrupt blob → shorter list, no throw; `SkillCatalog.Discover` on the
materialized directory returns exactly that one skill; resources present on disk **and** discovered
by the framework (not merely extracted).

### Step 6 — Wire it into `AgentGrain` (depends on 2, 5)

`AgentGrain.cs`: ctor dep + `_userSkills`, resolved in `OnActivateAsync` after `_mcpServers` and
**before** `BuildAgent`, commented in the same voice as the three existing ordering comments.
`AgentGrain.BuildAgent.cs`: merged catalog (shipped ⊕ user, upload wins, `OrdinalIgnoreCase` per
§9), used by both the `selectedSkillPaths` gate and `ExpandSkillsWildcard` (now an instance method);
add `DisableReadSkillResourceApproval = true`; apply §3's `SkillToolLimits` truncation wrapper.
Register per §12.

*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentGrainTests`. New tests:
uploaded skill advertised; an upload named `websearch` shadows the shipped one (one entry, the
user's description and path); a case-differing name shadows rather than duplicates;
`GetEnabledToolsAsync` on a null selection includes `skill:{uploadedName}`; a selection of only an
uploaded skill drops the shipped ones and keeps `load_skill`; zero selected skills still drops the
provider; **an oversized skill body and an oversized resource are truncated to `SkillToolLimits`**.

### Step 7 — Endpoints (depends on 4, 5)

New partial `src/Agent42.Backend/UserSkills/AgentApi.UserSkills.cs` — **and register it from
`src/Agent42.Backend/Api/AgentApi.cs`** (ADR 0040: an unregistered partial compiles cleanly and
silently deletes every endpoint in it). `GET /api/skills`, `POST /api/skills` (multipart, single
`file` part, `.DisableAntiforgery()`, reuse `MaxAttachmentBytes`), `DELETE /api/skills/{id}`.
Resolve `ISkillPackageStore` optionally via `IServiceProvider` (§12).

POST orchestrates: normalize → save blob(s) → grain upsert → delete superseded blob + directory →
**§5's auto-enable** → respond with installed skills, skipped entries, and a **shadowing warning**
when a shipped name was replaced (§11). Errors are `text/plain` on 400, matching the attachments
endpoint. Extend `src/Agent42.Backend/Tools/AgentApi.Tools.cs` to merge per-user skills
(upload-wins) into the descriptor list, reading metadata from the **grain only** — never blob or
disk.

*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~UserSkillsApiTests` — upload `.md`;
multi-skill zip; oversize → 400; garbage zip → 400; traversal name → 400; list; delete; re-upload
replaces; `GET /api/tools` shows the upload and shadows a same-named shipped skill; shadowing
warning present; auto-enable appends to a non-null default and leaves a null one null; another user
does not see them.

### Step 8 — Frontend (depends on 7)

`js/skills.js` (list / upload / delete / skipped + shadowing report), `dom.js` exports + the tab pair
**appended after Configuration** (§16), `index.html` tab + panel reusing `.providers__*` classes, a
UI note covering §15's next-new-chat staleness and §5's per-agent caveat, `main.js` `wireSkills()`,
`sw.js` SHELL + `CACHE` → `agent42-shell-v21`.

*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~UserSkillPickerAssetsTests` plus
existing `PwaAssetsTests`/`ProjectLayoutTests`. Source-text assertions (no JS runner in this repo):
module reachable from `main.js`; in `SHELL`; `CACHE` bumped; tablist keeps MCP at 3 and
Configuration at 4; `FrontEndWiredModules()` row added.

### Step 9 — Docs (depends on 6, 7, 8)

`/decide` → `.agentic/decisions/0049-upload-agent-skills.md`, declaring that it **amends ADR 0048**
(per-deployment → per-user), carrying §1's decisions with rejected alternatives, Step 1's measured
values, and §11's full security position including the two accepted residuals (shadowing-removes-a-
control; unbounded temp until restart). `/gotcha` for: the §15 ordering hazard (**and correcting ADR
0048's clause (a)** in the `null == all` entry), the §7 content-folder collision, and the §6
validate-before-write rule. Fix every claim in §14. Pay the §13 doc debt: the folder counts in
`CLAUDE.md:669`, ADR 0039 and ADR 0040 each move by one — a folder list is a factual inventory, not
a decision, so correct it in place and note it in ADR 0049's Consequences rather than superseding
either ADR. Add a CLAUDE.md bullet in the established voice.

*Verify:* `node .agentic/scripts/check-doc-paths.mjs` (the actual CI doc-path check —
`.github/workflows/pr-checks.yml`; there is no `DocPath` test). Then `/audit-docs` on touched entries.

### Step 10 — Manual verification in DevHost

`dotnet run --project tools/Agent42.DevHost`, drive headless Chrome over CDP (the ADR 0048
precedent — that leg **was** run and its values recorded; do the same). Upload a `.md`; upload a
two-skill zip; confirm both appear in the Tools picker; **press Save in the Tools picker first, then
upload a third skill, and confirm §5's auto-enable leaves it checked** — a fresh user has a `null`
selection and would take the wildcard path, hiding the bug this exists to catch. Confirm a shadowing
upload replaces the built-in's description and surfaces the warning. Delete one; confirm it leaves
the picker. Start a **new** chat before expecting an upload to affect a conversation (§15).

Record observed values in ADR 0049. If no browser driver is available, say so explicitly and record
it as an unverified residual — do **not** claim the leg was run (ADR 0043's failure mode).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A standalone `SKILL.md` uploads and installs | `Upload_BareMarkdown_Installs` | `SkillPackageTests.cs` / `UserSkillsApiTests.cs` |
| AC2 | A zip with several skill directories installs all of them | `Upload_MultiSkillZip_InstallsEach` | `SkillPackageTests.cs` |
| AC3 | Bundled resources survive **and are discovered by the framework** | `MaterializedSkill_ResourcesAreDiscoverable` | `UserSkillProviderTests.cs` |
| AC4 | Script and binary entries are dropped and reported | `Normalize_DropsScripts_AndReportsThem` | `SkillPackageTests.cs` |
| AC5 | Zip-slip, oversized, bomb, duplicate and reserved-name entries are handled | `Normalize_RejectsTraversal_AndCapsSize`, `Normalize_HandlesHostileEntryNames` | `SkillPackageTests.cs` |
| AC6 | A frontmatter `name` is validated **before** any filesystem write | `BareMarkdown_TraversalName_RejectedBeforeWrite` | `SkillPackageTests.cs` |
| AC7 | Tool return values are truncated to `SkillToolLimits` | `SkillTools_TruncateOversizedReturns` | `AgentGrainTests.cs` |
| AC8 | An uploaded skill appears in `GET /api/tools` as `skill:{name}` | `Tools_IncludesUploadedSkill` | `UserSkillsApiTests.cs` |
| AC9 | An uploaded skill shadows a shipped one, incl. case-differing names | `UploadedSkill_ShadowsShippedSkill` | `AgentGrainTests.cs` |
| AC10 | Shadowing a shipped skill surfaces a warning | `Upload_ShadowingShippedSkill_Warns` | `UserSkillsApiTests.cs` |
| AC11 | Upload auto-enables into a non-null default; leaves a null one null | `Upload_AutoEnables_OnMaterialisedSelection` | `UserSkillsApiTests.cs` |
| AC12 | An uploaded skill's guidance reaches the model | `UploadedSkill_IsAdvertisedToTheModel` | `AgentGrainTests.cs` |
| AC13 | Selecting only an uploaded skill drops shipped ones, keeps `load_skill` | `PerSkillSelection_AcrossBothLayers` | `AgentGrainTests.cs` |
| AC14 | A null selection expands to include uploaded skill keys | `GetEnabledTools_NullSelection_IncludesUploadedSkills` | `AgentGrainTests.cs` |
| AC15 | Zero selected skills still drops `AgentSkillsProvider` | existing `SkillsDisabled_DropsLoadSkill_KeepsRetrieval` (extended) | `AgentGrainTests.cs` |
| AC16 | Skills are per-user: another user does not see them | `Skills_AreScopedToTheUploader` | `UserSkillsApiTests.cs` |
| AC17 | Re-upload of the same name replaces rather than duplicates | `Upload_SameName_Replaces` | `UserSkillsGrainTests.cs` |
| AC18 | Delete removes grain row, blob, directory and picker entry | `Delete_RemovesFromCatalog` | `UserSkillsApiTests.cs` |
| AC19 | Uploaded skills are resolved on activation before the first `BuildAgent` | `UserSkills_ResolvedOnActivation_BeforeFirstBuildAgent` | `AgentGrainTests.cs` |
| AC20 | A corrupt/missing bundle degrades to a shorter list, never throws | `Provider_ToleratesCorruptBundle` | `UserSkillProviderTests.cs` |
| AC21 | The endpoint partial is reachable from the composition root | `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` (+1 row) | `ProjectLayoutTests.cs` |
| AC22 | Frontend module in the import graph, in `SHELL`, `CACHE` bumped, tab order preserved | `UserSkillPickerAssetsTests`, `FrontEndWiredModules` (+1 row) | `UserSkillPickerAssetsTests.cs`, `ProjectLayoutTests.cs` |
| AC23 | The new feature folders are in the guarded folder set for both projects | `FeatureFolderSet_IsExactlyWhatTheAdrDescribes` (extended) | `ProjectLayoutTests.cs` |
| AC24 | The new persisted state's Cosmos `$type` resolves and round-trips | `PersistedTypeName_StillResolves`, `UserSkillsState_RoundTripsFieldForField` | `GrainStorageSerializerTests.cs`, `SerializationCompatibilityTests.cs` |

AC3's *model-facing* half (that `read_skill_resource` returns the content to the model) and the
`run_skill_script` advertisement question are proven by the **Step 1 spike**, not by an in-repo
test — the same honest treatment ADR 0037/0043 give their untestable halves. Say so in the ADR.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (`cb77bd2`):

| Gate | Threshold |
|---|---|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `dependencies.vulnerable_packages` | 0 (hard block) |
| `dependencies.outdated_packages` | report (soft) |
| `dependencies.add_policy` | latest-stable — resolved at add time, never from memory |
| Branch policy | `branches.protected: [main]` — feature branch + MR, never commit to main |
| Local enforcement | `soft_warning` (render the table, ask proceed/abort on FAIL) |
| CI enforcement | `hard_block` on build, tests, `dotnet format`, vulnerable packages, doc-path integrity |
| Coverage tooling | `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` (coverlet) |

`SkillPackage.Normalize` is the one place likely to approach `cognitive_complexity_max: 15` — split
the entry-classification predicate out if it does.

No new NuGet package is needed: `System.IO.Compression.ZipArchive` is in the BCL.

## Out of scope

- **Executing skill scripts.** No `AgentFileSkillScriptRunner` is registered and none is added.
- **Sharing a skill between users**, publishing to a catalog, or importing from a URL/registry.
- **Editing a skill in the browser.** Replace = re-upload.
- **Versioning or rollback** of an uploaded skill.
- **Rewriting per-conversation overrides or per-agent snapshots** on upload — §5 auto-enables the
  *user default* layer only; the other two layers keep ADR 0048's divergence, now applying to
  uploaded skills too.
- **Changing `null == all` semantics.** The legacy `skills` wildcard keeps meaning "every skill",
  now including uploaded ones. No migration. (`ExpandSkillsWildcard` changes from static to
  instance and widens its expansion *set*; the meaning is preserved.)
- **`excludedPaths` changes.** New endpoints sit behind Easy Auth; ADR 0042's list is unchanged.
- **An eviction scheme for the temp materialization cache** (§4).

## Review

A `critic` subagent reviewed this plan against the repo's knowledge files before approval. Findings
and disposition:

**Accepted and folded in** — the tool-return 2 MB budget (§3, AC7, `SkillToolLimits`);
validate-name-before-write path traversal (§6, AC6); uploaded skills default-OFF for anyone who
pressed Save (§5, AC11, Step 10); shadowing can remove the `websearch` mitigation (§11, AC10);
missing `SerializationCompatibilityTests` / `GrainStorageSerializerTests` / `FrontEndWiredModules`
rows (§13, AC22/AC24); folder-count prose debt (§13, Step 9); the non-existent `DocPath` verify
command (Step 9 → `node .agentic/scripts/check-doc-paths.mjs`); resource folder/extension scoping
(§10, Step 1 item 5); extra zip guards — duplicates, case-only collisions, reserved names, install
count cap (§10); temp cleanup (§4); the `CanGenerateSasUri` Azurite bootstrap (Step 3); the lighter
`IServiceProvider` registration pattern for the endpoint (§12); doc claims falsified (§14);
ADR 0049 amends 0048 (§14, Step 9); activation staleness (§15); the stale `Skills/` bullet in
Affected areas (removed).

**Accepted as a question, not an answer** — the critic asserted `run_skill_script` is registered
only when a skill actually has scripts, contradicting this plan's original claim that it stays
advertised-but-inert. Neither reading could be confirmed from the pinned assembly (`BuildTools`
exists as a symbol; the parameter names are not in the string heap). Rather than swap one unverified
claim for another, §11 forbids asserting either and Step 1 item 3 measures it. Same treatment for
`MaxSearchDepth` (Step 1 item 4) and the `SkillCatalog.Discover` nested-path doc-comment, which the
critic believes is inaccurate — the spike settles it and the comment is reconciled either way.

**Rejected** — the suggestion to put this code in the existing `Tools/` folders to avoid the §13
folder-list churn. Those guard lists exist precisely so that adding a feature forces the docs to be
updated; routing around them to save three edits inverts the mechanism. Rationale recorded in §7.
