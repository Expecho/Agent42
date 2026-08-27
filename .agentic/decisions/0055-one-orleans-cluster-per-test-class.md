---
id: 0055
date: 2026-08-27
status: accepted
tags: [testing, orleans, performance, no-behaviour-change]
---

# Title: Deploy one Orleans TestCluster per test class, not per test

## Context

Every Orleans-backed class in `tests/Agent42.Tests` deployed a **fresh two-silo `TestCluster` per test
method**, because xUnit v2 constructs a new instance of the test class for each test and therefore runs
`IAsyncLifetime.InitializeAsync` per test. Thirteen of those classes additionally built a full ASP.NET
host (`WebApplication.CreateSlimBuilder` + `MapAgentApi` + `StartAsync`) per test.

The suite took **7m38s**. The measurement that decided the approach is that **it is latency-bound on
cluster startup, not CPU-throughput-bound**: the same suite with `xUnit.ParallelizeTestCollections=false`
took 9m05s, a **1.2x return on 32-way parallelism**. Summed per-test time was 24.8 min under parallel
contention against ~9 min serially, so parallelism inflated per-test cost almost as fast as it removed
wall-clock.

**Consequence: reducing the NUMBER of deploys is the only lever that works.** Tuning `MaxParallelThreads`,
adding cores or splitting the assembly were each measured worth ~90 seconds total, and are not pursued.

## Decision

**A class fixture, not a collection fixture, and not cluster-per-test.**

`SharedClusterFixture` deploys the cluster once and xUnit hands the same instance to every test in the
class. A **collection** fixture would go further, one cluster for many classes, and is deliberately
rejected twice over: xUnit runs each class as its own collection, so per-class is what keeps the cluster
classes running *in parallel*; and, more importantly, per-class keeps each class's accumulated grain state
class-local, which is the property the three safety checks below rely on.

### What replaces "every test starts against a virgin cluster"

That guarantee is gone, and nothing restores it. It is replaced by three narrower properties, **all three
of which must be checked per class** before converting it:

1. **Grain keys are unique per test.** Isolation comes from the key, not from cluster freshness.
2. **No assertion depends on a mutable per-silo singleton the reset does not clear.**
3. **No cluster-wide aggregate assertion that a stray reminder could perturb** (see the reminder note below).

A class failing any of the three is **left cluster-per-test**. That is a normal outcome, not a failure, and
six classes ended there.

### The reset is structural, not conventional

Property 2 is not a property of the cluster; it is a call someone has to remember to make. A converted class
needing no other per-test setup would carry no `IAsyncLifetime` at all and so would never call
`ResetSiloState()` - correct the day it is written, silently wrong the day a test that reads a counter is
added to it.

So the cluster is reachable **only** through `SharedClusterTests<TFixture>`, whose `InitializeAsync` resets
first and is not overridable (an implicit interface implementation is `virtual final` in IL, so no derived
class can take that slot). `SharedClusterSeamTests` asserts the convention the type system cannot: every
concrete class implementing `IClassFixture<T>` for a `T : SharedClusterFixture` must inherit some closed
form of `SharedClusterTests<>`.

**That guard is easy to write blind, and the first version of it was.** `Type.GetInterfaces()` returns
*closed* generic types, so a class taking `IClassFixture<AgentApiFixture>` never yields
`IClassFixture<SharedClusterFixture>`: comparing by identity matched the twenty-two plain-grain classes and
was silently blind to all eight API classes — exactly where the new fixture surface is. It now opens the
generic and tests the argument for assignability, and carries two checks against its own vacuity: that it
matched more than twenty classes at all, and that at least one of them uses a fixture that is *not*
`SharedClusterFixture` itself.

`ResetSiloState()` clears `FakeChatClient`, `RecordingPushNotificationSender` **and**
`SingleUserMcpToolProvider.Servers` - the last has no `Reset()` of its own because it lives in `src/` and
serves offline hosts too, so the fixture clears its settable state directly. The in-memory attachment,
document, skill and calendar stores are deliberately **not** on that list: they are process-global rather
than per-silo and already outlived every cluster, so a fresh cluster was never their isolation boundary and
sharing one changes nothing for them.

### The API hosts get a fixture per class, not one host registering everyone's services

`MapAgentApi` maps every endpoint and the route matcher infers all of their parameters up front, so a host
must register services for endpoints its tests never call: an unresolvable service parameter is treated as a
request body and breaks routing for the **whole** host. That part unions cleanly and lives in
`SharedApiFixture.AddRouteMatcherServices`.

What does **not** union is that a few of those registrations are *inputs a test asserts on*.
`AgentApiTests` reads its model catalog back out of `/api/models` and its `PushConfig` out of
`/api/push/config`, and needs values the other seven classes do not; the two OAuth classes need a fake
token endpoint and a connection-aware calendar double, which are behaviour rather than filler.

**This is a legibility choice, not a correctness one, and the first draft of this ADR overstated it.** It
originally claimed that registering `ISkillPackageStore` in a shared host would leak `UserSkillsApiTests`'
uploads into `ToolSelectionApiTests`' `/api/tools` assertions. That is false: `/api/tools` reads the
uploads from `IUserSkillsGrain` (`src/Agent42.Backend/Tools/AgentApi.Tools.cs`), never from the package
store, so the list is already per-class by virtue of the cluster being per-class — and the in-memory store
is registered in every silo today with no observed leak. A union host was genuinely feasible. What it
costs is that every host silently carries one class's expectations, with no way to express a second class
wanting different ones, so reading a host tells you nothing about which class asked for what.

So `SharedApiFixture` is a base with sensible defaults, and four subclasses supply what their class actually
needs (registration order is the override mechanism: a later `AddSingleton` of the same service type wins).
This costs no extra deploys, because xUnit gives every class its own fixture instance regardless of the
fixture type. **Group C is where sharing a host is actually impossible** — there the host's registrations
are the variable under test, and no union can express that.

### Live reminders outlive a test in a shared cluster

`AgentGrain` registers a **real** Orleans reminder with a 1-minute minimum, backed by
`UseInMemoryReminderService()`, and nothing cancels them. Under cluster-per-test the cluster died in ~1.3s
so none ever fired. Shared, a class running longer than a minute fires one mid-test, and `ReceiveReminder`
pushes into the recorder - a **CI-only** flake, because CI runs under `--collect:"XPlat Code Coverage"` on a
hosted runner. Fixed in the tests, by filtering push assertions per userId, rather than by cancelling
reminders in `src/`: a production change is not warranted by a test problem.

### Three test bodies changed, all for the same reason

The per-class gate is a review, not a run, and a review of thirty classes found three places where a grain
key was shared between tests in one class. All three are now named per test:

- `AgentApiTests.ProfileEndpoint_ReturnsJson_WithDefaultWhenUnset` sent no principal header, so it read the
  shared `local-dev` fallback profile the neighbouring PUT tests write. This one **failed loudly** on the
  class's first shared run.
- `AgentApiTests.PushSubscription_Post_RejectsIncomplete` asserted `Empty` on the same `local-dev` push
  grain that `PushSubscriptionEndpoints_Crud` writes. It passed only because `Crud` deletes what it adds —
  a property of that test's body, not of this one, and wrong the moment a third push test is added.
- `GoogleCalendarServiceTests` used one `user@example.com` key across nine tests. Every one of them
  happened to call `ConnectAsync` first, so `SetAsync` overwrote the stale row; but the key ring is
  deliberately per-test, so a test that ever read without connecting would have hit a previous test's
  ciphertext under a different ring and failed as an unexplained `NotConnected`. The key is now unique per
  test instance, which is what pairs it with the ring.

None of the assertions changed. What changed is that the preconditions are properties of the test that
states them rather than of which test ran first.

## Classes deliberately not converted

- **`UsageRollupGrainTests`, `UsageIngestionServiceTests`, `UsageApiTests`** - all three resolve
  `GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)`: **one grain per cluster, a production
  constant**. They assert absolute totals against it (300 input tokens versus 0 tool calls; one is
  literally named `Usage_EmptyStore_ReturnsZeroed`). Sharing a cluster breaks these **deterministically**,
  in whatever order they run, and there is no in-test fix: the key is a `src/` constant with no reset.
- **`ChatStreamDisconnectTests`** - one test, zero deploys saved, and ADR 0046 requires a real Kestrel
  server plus a raw socket.
- **`AttachmentRehydrateTests`** - one test, and the only class not using `TestSiloConfigurator`.
- **`ActivityPropagationTests`** - builds two clusters with bespoke configurators and compares them. The
  comparison *is* the test.

`DiagnosticsApiTests` and `ConfigHealthApiTests` share the **cluster only**: each keeps building its own
hosts, because which registrations a host carries is the variable under test (a capture buffer present or
absent; a counting probe set whose count is the assertion). `SpeechApiTests` is the same shape for the same
reason - its `HostAsync` registers the chat factory only when a speech service is supplied, because its two
tests are "returns audio" and "409 when unconfigured", and one host cannot be both.

`AgentGrainTests` keeps eleven in-test `NewClusterAsync` clusters for bespoke configurators. **"One cluster
per class" is the fixture's contract, not a property of the class.**

## Results

Measured on this machine (i9-13980HX, 24C/32T, AC power), 956 tests:

| Run | Before | After |
|-----|--------|-------|
| Full suite, parallel | **7m38s** | **31s / 34s / 36s** (three runs) |
| Full suite, serial (`ParallelizeTestCollections=false`) | 9m05s | **1m12s** |
| Full suite, coverage + `MaxParallelThreads=2` | not measured | **49s** |

All five runs green at 956/956. Thirty test classes converted; six excluded and untouched.

## What the flakiness gate does NOT prove

`ParallelizeTestCollections=false` reorders **collections** (classes), not tests **within** a class - and
within-class order is the only ordering a class-scoped cluster newly exposes. xUnit's default orderer is
deterministic per test-case id, so repeated runs re-use one fixed order. The gate therefore **cannot**
prove within-class order independence; it can only catch what the fixed order happens to expose. What
backs that property is the per-class review of the three checks above, not the four runs.

## Alternatives rejected

- **A collection fixture across classes** - serializes the cluster classes and makes state cross-class,
  trading one bottleneck for another and defeating the safety checks.
- **Tuning xUnit parallelism** - measured worth ~90 seconds.
- **Reducing silos from two to one** - a real lever, but it changes grain **placement**, which several
  tests reason about (`AgentGrainTests` sums fakes across `Cluster.Silos` because the grain may activate
  anywhere). Separate plan.
- **Cancelling reminders in `src/`** to make the reminder problem structurally impossible - a production
  change to solve a test problem.

## Consequences

- The suite is **~13x faster**, which changes what per-step test gating costs during development.
- A new Orleans test class must now make a decision it did not have to make before: shared cluster or not.
  The base class and the seam guard make the safe path the default one, and the three checks above are the
  question to answer.
- A future test that asserts on a **cluster-wide aggregate** inside a converted class is newly wrong, and
  nothing in the type system will say so.
