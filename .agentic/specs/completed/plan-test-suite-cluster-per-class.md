# Plan — deploy one Orleans cluster per test class, not per test

planned-against: `main @ 5b9aff6`; steps 0a/0b already committed on
`fix/agent-grain-tests-class-fixture @ 7fbc368`, `@ 7d4ce2d`

**Revision 2** — rewritten after an adversarial `critic` pass. Three HIGH findings changed the scope:
three classes were removed from conversion entirely, two were downgraded to cluster-only sharing, and a
CI-only flake in the already-committed step 0 was found and fixed (step 0b). The critic's findings are
folded into the facts and steps below rather than kept as a separate list — **the group lists in
"Affected areas" are the corrected ones.**

## Overview

Every Orleans-backed test class in `tests/Agent42.Tests` deploys a **fresh two-silo `TestCluster` per
test method**, because xUnit v2 constructs a new instance of the test class for each test and therefore
runs `IAsyncLifetime.InitializeAsync` per test. Thirteen of those classes additionally build a full
ASP.NET host (`WebApplication.CreateSlimBuilder` + `MapAgentApi` + `StartAsync`) per test.

Converting each class to an `IClassFixture` collapses that to **one deploy per class**.

Step 0a (commit `7fbc368`) converted the worst offender as proof of value: **`AgentGrainTests`
2m17s → 9s**, 109 green, stable over four runs. Step 0b (commit `7d4ce2d`) fixed the flake that
conversion introduced. This plan rolls the pattern across the remaining **36 classes / 236 tests**.

## Affected areas

Verified against the tree at `7d4ce2d`. **Every file is `tests/Agent42.Tests/**` — no `src/` change, no
infra, endpoint, grain, Bicep, secret, `excludedPaths` entry or `sw.js` `CACHE` bump.**

**Already delivered:**
- `SharedClusterFixture.cs` (new) — cluster deployed once per class + `ResetSiloState()`
- `AgentGrainTests.cs` — `IClassFixture<SharedClusterFixture>, IAsyncLifetime`; push assertions
  filtered by userId (`SendsFor`) per fact 8
- `FakeChatClient.cs`, `RecordingPushNotificationSender.cs` — `Reset()`

**Group A — plain grain classes: full sharing (18 classes, 112 tests).** Standard
`TestSiloConfigurator`, no HTTP host. Consume `SharedClusterFixture` unchanged:

`GoogleCalendarServiceTests`(15) · `UserMcpToolProviderTests`(11) · `UserProfileGrainTests`(9) ·
`UserSkillsGrainTests`(8) · `CalendarToolTests`(8) · `WebPushNotificationSenderTests`(7) ·
`UserSkillProviderTests`(7) · `UserProviderConfigGrainTests`(7) · `UserMcpConfigGrainTests`(5) ·
`UserGoogleGrainTests`(5) · `ShareLinkGrainTests`(5) · `AgentRegistryGrainTests`(5) ·
`UserPushGrainTests`(4) · `McpOAuthPendingGrainTests`(4) · `GrainStorageSerializerTests`(4) ·
`GoogleOAuthPendingGrainTests`(4) · `GrainIdentityTests`(2) · `AgentKeyDirectoryGrainTests`(2)

⚠ `UserMcpToolProviderTests` needs the fact 5 check: `SingleUserMcpToolProvider.Servers`/`Tools` are
**mutable per-silo singleton state that `ResetSiloState()` does not currently reset.**

**Group B — API classes: cluster + host + client shared (8 classes, 99 tests).** Need `SharedApiFixture`
(step 2):

`AgentApiTests`(39) · `UserSkillsApiTests`(17) · `ProvidersApiTests`(12) · `GoogleOAuthEndpointTests`(9) ·
`McpServersApiTests`(8) · `ToolSelectionApiTests`(6) · `AttachmentTests`(5) · `McpOAuthEndpointTests`(3)

⚠ `AgentApiTests` builds a **third host inside a test body** (`AgentApiTests.cs:872-890`) because the
shared host registers a service enabled. That test keeps its own host; only `InitializeAsync` converts.

**Group C — cluster-only sharing (2 classes, 8 tests).** The **host is the variable under test**, so
only the `TestCluster` moves to a fixture; each test keeps building its own `WebApplication`:
- `DiagnosticsApiTests`(5) — builds **two** hosts (`_withCapture`, `_withoutCapture`,
  `DiagnosticsApiTests.cs:49-51`) around a per-test `LiveTelemetryBuffer` (`:45-47`) whose contents are
  asserted on. A shared buffer is exactly the mutable cross-test state fact 5 is about.
- `SpeechApiTests`(3) — `HostAsync(ISpeechService?)` (`SpeechApiTests.cs:40-51`) registers the chat
  factory **only when a speech service is supplied**, because the two tests are "returns audio" (`:81`)
  and "409 when unconfigured" (`:99`). One host cannot be both.

**Excluded — well-known global grain key (3 classes, 9 tests). DO NOT CONVERT.**
`UsageRollupGrainTests`(3) · `UsageIngestionServiceTests`(2) · `UsageApiTests`(4).
All three resolve `GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)` — **one grain per cluster,
a production constant** (`UsageRollupGrainTests.cs:26`, `UsageIngestionServiceTests.cs:46,72`,
`UsageApiTests.cs:118`). They assert absolute totals against it: `Assert.Equal(300, …InputTokens)`
(`UsageRollupGrainTests.cs:41`) versus `Assert.Equal(0, …CallCount)` (`:53`), and `UsageApiTests.cs:135`
is literally named `Usage_EmptyStore_ReturnsZeroed` (`:141-142`). Sharing a cluster breaks these
**deterministically**, in whatever order they run. There is no in-test fix: the key is a `src/` constant
with no reset, and `src/` changes are out of scope.

**Excluded — other (3 classes, 4 tests).**
- `ChatStreamDisconnectTests`(1) — one test, zero deploys saved; and ADR 0046 requires a **real Kestrel
  server + raw socket** (TestServer's in-memory pipe cannot reproduce a dead transport, and the test
  passes there against a broken endpoint). Do not touch.
- `AttachmentRehydrateTests`(1) — one test, and the one class not using `TestSiloConfigurator`
  (`CountingStoreSiloConfigurator`, `:66`).
- `ActivityPropagationTests`(2) — builds **two** clusters with bespoke configurators (`:36-41`) and
  compares propagating vs plain. The comparison *is* the test.

**Conditional (2 classes, 4 tests):** `RequestContextPropagationTests`(2) · `ConfigHealthApiTests`(2) —
convert only if steps 1/2 make it a two-line change; a 2→1 deploy saving does not justify bespoke work.

## Context & constraints

### Facts measured during planning — trust these, do not re-derive

Measured on this machine (i9-13980HX, 24C/32T, AC power) against the tree at `7fbc368`/`7d4ce2d`.

1. **The suite is latency-bound on cluster startup, NOT CPU-throughput-bound.** Measured: full suite
   **parallel = 7m38s**, **serial (`xUnit.ParallelizeTestCollections=false`) = 9m05s** — a **1.2× return
   on 32-way parallelism.** Summed per-test time was **24.8 min** under parallel contention but ~9 min
   serially: parallelism inflates per-test cost almost as fast as it removes wall-clock. **Consequence:
   reducing the NUMBER of deploys is the only lever that works. Do not tune `MaxParallelThreads`, add
   cores, or split the assembly — measured worth ~90 seconds total.**
2. **A cluster deploy costs ~1.2s uncontended** (step 0a: removing 108 deploys took 137s → 9s). The
   estimate for this plan is deliberately **not** stated as a result — step 5 measures it.
3. **xUnit v2 (2.9.3) constructs a new test-class instance per test method**, so `IAsyncLifetime` on the
   class is **per test**. That is the entire bug; `IClassFixture<T>` is the per-class seam.
4. **Isolation comes from unique grain KEYS — but this is NOT universal.** It holds for `AgentGrainTests`
   (verified: `purge-conversation`'s two occurrences are both inside one test, `:237`/`:251`). It is
   **false** for the three Usage classes above, which share a well-known global key. **The step-1 gate
   must be run per class and its answer must not be assumed from this plan's grouping.**
5. **The mutable-shared-state list is NOT just the two fakes with `Reset()`.** Also live:
   - `SingleUserMcpToolProvider.Servers`/`Tools` — settable, per-silo singleton
     (`TestSiloConfigurator.cs:82-83`), **not reset by `ResetSiloState()`**. `AgentGrainTests` dodges it
     only by building throwaway clusters (`:2185`, `:2269`).
   - `InMemoryAttachmentStore`, `InMemoryDocumentIndex`, `InMemorySkillPackageStore`,
     `InMemoryCalendarService` are **`static`/process-global and already leak today** — no regression,
     but proof that "a fresh cluster" was never the isolation boundary this plan first claimed.
6. **Tests within a class run serially** (one class = one collection), so `ResetSiloState()` cannot race
   a test in flight. This bounds *test-driven* interference only — see fact 8.
7. **`MapAgentApi` eagerly infers every endpoint's parameters**, so Group B hosts must register services
   for endpoints they never call. A shared host must register the **union**. But fact 3 of the critic
   pass showed the harder problem is Group C: sometimes the host's registrations are *the variable under
   test*, and no union can express that.
8. **Live Orleans reminders outlive a test in a shared cluster.** `AgentGrain.Scheduling.cs:53-61`
   registers a **real** reminder with a **1-minute minimum**, backed by `UseInMemoryReminderService()`
   per silo (`TestSiloConfigurator.cs:54`); nothing cancels them, and `AgentGrainTests` registers 12.
   Under cluster-per-test the cluster died in ~1.3s so none fired. **Shared, any class running longer
   than a minute fires one mid-test**, and `ReceiveReminder` pushes into the recorder. This is a
   **CI-only flake** — the class is ~9s here but CI runs `--collect:"XPlat Code Coverage"` on a hosted
   runner (`pr-checks.yml:76`). Fixed in step 0b by filtering sends per userId. **Constraint for every
   later conversion: a shared-cluster class must either leave no live reminders behind, or assert on
   state scoped to its own user/grain — never on a cluster-wide aggregate.**
9. **`SonarAnalyzer.CSharp` S3776 is at `error`** (`.editorconfig:30`, ADR 0051) and
   `GlobalPackageReference` applies it to the **test project too**. A bulky `SharedApiFixture` is the one
   place this plan can trip a blocking gate. Keep it decomposed.

### What makes sharing safe, and where it stops

"Every test starts against a virgin cluster" is replaced by three narrower properties, **all three of
which must be checked per class**: unique grain keys (fact 4), no assertion on unreset mutable
singletons (fact 5), and no cluster-wide aggregate assertion that a stray reminder can perturb (fact 8).
A class failing any of them is **left cluster-per-test** — that is a normal outcome, not a failure.

**Do not** use a collection fixture shared across classes: xUnit runs each class as its own collection,
so per-class keeps them parallel, and — more importantly — it keeps each class's state class-local,
which is the property facts 4/5/8 rely on.

## Steps

Per-step gating: **a step is not done until its filter is green AND its timing is recorded.**

**Step 1 — Group A (18 classes).** One class at a time: replace the `IAsyncLifetime`/`TestClusterBuilder`
block with `IClassFixture<SharedClusterFixture>`, take the fixture in the constructor, assign `_cluster`.

*Per-class gate — run it, do not assume it from this plan:* (a) grain keys unique per test, including
any well-known/global key; (b) no assertion on a per-silo singleton that `ResetSiloState()` does not
reset (fact 5 — `UserMcpToolProviderTests` is the known case); (c) no cluster-wide aggregate assertion
plus a live reminder (fact 8). Any failure → skip the class, record why.
*Verify:* `dotnet test --filter FullyQualifiedName~<Class>` after each.

**Step 1b — make the reset unavoidable.** Step 1 will leave classes that need no per-test cleanup with no
`IAsyncLifetime` at all, so they never call `ResetSiloState()` — safe today, silently unsafe for the next
class added. Change the seam so reset cannot be forgotten (fixture exposes the cluster only through a
resetting accessor, or a small base class). Resolves the AC3 contradiction the critic raised.
*Verify:* full suite green; a new class cannot compile its way past the reset.

**Step 2 — `SharedApiFixture` (new).** Owns `TestCluster` + `WebApplication` + `HttpClient`, registering
the **union** of the eight Group B classes' services (fact 7). Read all eight `InitializeAsync` bodies
before writing it. Mind fact 9 — decompose rather than writing one long method.
*Canary:* convert **`McpServersApiTests` or `GoogleOAuthEndpointTests` first, NOT `ProvidersApiTests`** —
`ProvidersApiTests.cs:36-63` is the plainest of the eight and would not surface a missing registration.

**Step 3 — Group B remainder**, one at a time, same gate. `AgentApiTests`(39) **last** — largest, and it
keeps its own in-test host at `:872-890`.

**Step 4 — Group C**, cluster-only sharing for `DiagnosticsApiTests` and `SpeechApiTests`.

**Step 5 — flakiness gate.** Shared state makes order-dependence newly possible. Required runs:
- full suite ×3 (parallel);
- once with `xUnit.ParallelizeTestCollections=false`;
- **once under `--collect:"XPlat Code Coverage"` with parallelism constrained** (e.g.
  `-- xUnit.MaxParallelThreads=2`) — fact 8's flake only appears when a class runs over a minute, which
  is CI's configuration, not this machine's.

⚠ **Known limitation, do not paper over it:** `ParallelizeTestCollections=false` reorders *collections*
(classes), not tests **within** a class — and within-class order is the only ordering a class-scoped
cluster newly exposes. xUnit's default orderer is deterministic per test-case id, so repeated runs
re-use one fixed order. This gate therefore **cannot** prove within-class order independence; it can
only catch what the fixed order happens to expose. Record that honestly in the ADR rather than claiming
AC4 proves more than it does.

**Step 6 — measure and record.** Full suite parallel + serial, timed. These are the ADR's numbers.

**Step 7 — docs (hard rule 2, same MR).**
- **New ADR** (`.agentic/decisions/0055-*.md`): decision is *class fixture over collection fixture over
  cluster-per-test*; the rationale worth preserving is fact 1 (deploy **count** is the only lever) plus
  the three-property safety test and the classes deliberately excluded with reasons.
- **`.agentic/gotchas.md`** — and note the ≤40-entry guardrail, so justify each:
  (i) xUnit v2 runs `IAsyncLifetime` **per test**, with `SharedClusterFixture` named as the exemplar;
  (ii) fact 8, live reminders outliving a test in a shared cluster — the non-obvious one;
  (iii) **refresh the existing exemplar row** `.agentic/gotchas.md:27` ("Grain test (Orleans TestCluster
  + fake client) | `AgentGrainTests.cs` | 2026-07-07") — step 0a changed that canonical shape, so the
  row and its `Verified` date are now stale doc debt.
  Fold `ChatStreamDisconnectTests`' exclusion into (i) rather than its own entry — ADR 0046 already
  states it (`0046-….md:72-73`) and the guardrail is real.
- **`CLAUDE.md`**: no change expected — confirm, do not assume.

## Acceptance criteria → evidence

| # | Criterion | Evidence |
|---|---|---|
| AC1 | No behaviour change | `dotnet test Agent42.slnx` — 954+ green |
| AC2 | Each converted class deploys one cluster **from its fixture** | `InitializeAsync` no longer calls `TestClusterBuilder`. **Does not mean one cluster per class**: `AgentGrainTests` deliberately keeps 11 in-test `NewClusterAsync` clusters for bespoke configurators; Group C keeps per-test hosts |
| AC3 | Mutable per-silo fakes reset between tests | Step 1b makes it structural, not conventional |
| AC4 | No order-dependent flakiness **that the gate can reach** | Step 5's four runs. Scope limit stated above |
| AC5 | Measurable improvement | Step 6 timings, recorded in the ADR |
| AC6 | Excluded classes untouched | `git diff --stat` shows no change to the 6 excluded |
| AC7 | Lint + analyzers clean | `dotnet format --verify-no-changes`; S3776 at `error` must not fire |

No new unit test asserts "a cluster is deployed once" — the suite's runtime is the measurement, and such
a test would pin the implementation rather than the property.

## Out of scope

- **Any `src/` change.** If a conversion seems to need one, stop.
- Tuning xUnit parallelism (fact 1: ~90s).
- Reducing silos from 2 to 1 — a real lever, but it changes grain **placement**, which several tests
  reason about (`AgentGrainTests` sums fakes across `_cluster.Silos` because the grain may activate
  anywhere). Separate plan.
- Cancelling reminders in `src/` to make fact 8 structurally impossible. Step 0b's per-user filtering
  solves it in tests; a production change is not warranted by a test problem.
- The unexplained 60-minute figure. Ruled out: coverage (7m58s), build/restore, Defender (real-time off),
  drive type (local fixed disk), extra projects (one), parallelism config. Fact 1 supplies a plausible
  mechanism — runtime scales nearly linearly with per-deploy latency and has no parallel headroom — but
  this plan does not claim to close it.

---

## Implementation divergences (recorded at execution, 2026-08-27)

Executed on `fix/agent-grain-tests-class-fixture` (the branch already carrying steps 0a/0b), not a new
`feature/*` branch — the spec's own header names that branch as the base.

**1. Step 1b ran BEFORE step 1, and produced a base class rather than a resetting accessor.**
Converting 18 classes onto a seam that step 1b was about to change would have meant editing each twice.
The seam is `SharedClusterTests<TFixture>` (new file), whose `InitializeAsync` calls `ResetSiloState()`
and is not overridable; a non-generic `SharedClusterTests` alias keeps the plain-grain case a one-word
base clause. A "resetting accessor" on the fixture was rejected: reset must happen once per test, not
once per property read. Because the type system cannot forbid taking `SharedClusterFixture` directly,
`SharedClusterSeamTests` (new) asserts it by reflection. Its first version compared interfaces by
identity against `IClassFixture<SharedClusterFixture>`, which — because `GetInterfaces()` returns closed
generics — matched the plain-grain classes and was blind to all eight API ones; a `critic` pass caught
that. It now opens the generic and tests the argument for assignability, with two anti-vacuity checks
(a minimum match count, and that at least one match uses a fixture subclass).

**2. Step 2 did NOT build one host registering the union of the eight Group B classes' services.**
The route-matcher filler (fact 7) unions cleanly and lives in `SharedApiFixture.AddRouteMatcherServices`.
What does not is that a few registrations are inputs a test asserts on: `AgentApiTests`' model catalog
(read back from `/api/models`) and `PushConfig` (read from `/api/push/config`), and the two OAuth
classes' behavioural doubles. Shipped: `SharedApiFixture` (defaults) + four subclasses
(`AgentApiFixture`, `UserSkillsApiFixture`, `GoogleOAuthApiFixture`, `McpOAuthApiFixture`). Same
one-host-per-class result; xUnit gives each class its own fixture instance regardless, so it costs
nothing.

**This entry originally claimed more than it could support**, and the `critic` pass falsified it: it said
registering `ISkillPackageStore` in a shared host would leak `UserSkillsApiTests`' uploads into
`ToolSelectionApiTests`' `/api/tools` assertions. `/api/tools` reads uploads from `IUserSkillsGrain`
(`src/Agent42.Backend/Tools/AgentApi.Tools.cs`), never from the package store, so that list is already
per-class via the cluster. A union host was feasible; the split is a legibility choice, and only Group C
genuinely cannot share a host.

**3. Three test bodies changed, all shared grain keys within one class.**
`AgentApiTests.ProfileEndpoint_ReturnsJson_WithDefaultWhenUnset` read the shared `local-dev` fallback
profile its neighbouring PUT tests write — it failed loudly on the class's first shared run, which is
the outcome the step-1 gate is for. Two more were found only by the `critic` pass, both passing by
accident: `AgentApiTests.PushSubscription_Post_RejectsIncomplete` asserted `Empty` on the same
`local-dev` push grain `PushSubscriptionEndpoints_Crud` writes (safe only because `Crud` cleans up after
itself), and `GoogleCalendarServiceTests` shared one `user@example.com` key across nine tests (safe only
because every one of them connects before reading — and paired with a deliberately per-test key ring, so
a future read-without-connect would have failed as an unexplained `NotConnected`). All three now name a
key per test; no assertion changed.

**4. `ResetSiloState()` extended** to clear `SingleUserMcpToolProvider.Servers` (fact 5's known case).
`UserMcpToolProviderTests` turned out not to touch that singleton at all — it uses its own
`FakeMcpConnectionFactory` — so the class converted unchanged; the reset is there for the next class.
The `Tools` setter writes through to `Servers`, so there is no second field to clear.

**5. Both "Conditional" classes were converted.** `RequestContextPropagationTests` was exactly the
canonical shape (one command). `ConfigHealthApiTests` got cluster-only sharing, which stopped being
bespoke work once `DiagnosticsApiTests` established that shape in step 4 — its `ProbeCounter` is asserted
on per test, so its host stays per-test.

**6. No class was skipped**, but the per-class gate is a review, not a run, and it missed two of the
three shared keys in divergence 3 on the first pass. Group C and the two conditionals share the cluster
only. The six excluded classes are untouched — `git diff --stat` over them is empty (AC6).

**7. Pre-existing gate failure fixed in-branch.** `check-doc-paths.mjs` (a hard CI block) was already
failing on `main` over ADR 0054's `/logs/logs.js`, which is a runtime URL rather than a repo path.

**Results.** 956 tests green. Full suite **7m38s → 31s / 34s / 36s** parallel over three runs, **9m05s →
1m12s** serial, and **49s** under `--collect:"XPlat Code Coverage"` with `MaxParallelThreads=2`. The
suite never runs a class for over a minute in any of those, so fact 8's reminder window never opens.
