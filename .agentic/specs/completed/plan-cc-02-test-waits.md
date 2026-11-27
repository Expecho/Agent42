# Plan cc-02: one shared wait helper for tests; remove fixed sleeps

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md` (read its "Shared constraints" first)

## Overview

`tests/Agent42.Tests` has 93 `Task.Delay` lines in 45 files. Most are legitimate (timeout upper bounds, hang simulators,
simulated latency in fakes). The real problems are: **eight private copies of a poll-until helper**, and **fixed sleeps
followed by a negative assertion** (flaky by construction). Memory records a proven OTel test flake in this suite.
`FakeTimeProvider` is **rejected** (see master plan, `TestTimeProvider.cs` header).

## Affected areas (verified by scout, re-verify before editing)

Private poll helpers to fold into one: `AgentGrainTests.cs:1080 PollUntilAsync`, `AgentWebhookGrainTests.cs:251`,
`FleetAnalyzerToolTests.cs:351`, `FleetResultDeliveryTests.cs:70`, `FleetToolTests.cs:253`,
`McpOAuthTokenResolverTests.cs:213 WaitUntilAsync`, `ModelRouterGrainTests.cs:278 WaitUntil`,
`UsageIngestionServiceTests.cs:77 WaitUntilAsync`; existing public one `LocalBridgeTestSupport.cs:80 WaitUntilAsync`;
inline loop `ChatGatewayServiceTests.cs:296-302`. Fixed-sleep-then-negative-assert: `ChatTurnPipelineTests.cs:27`
(Delay 200 then `Assert.Empty(Sent)`), `ChatReplyStreamerTests.cs:24-26` (pacing). Top files by count:
`ChatTurnPipelineTests` 8, `ChatReplyStreamerTests` 7, `SignalDaemonTests` 7, `ChatGatewayServiceTests` 6.
**More copies found by the critic (fold these too):** `ChatGatewayServiceTests.cs:292 Until` (wrappers in
`ChatDeliveryTests.cs:415`, `ChatTurnPipelineTests.cs:742/744`), `FleetLessonServiceTests.cs:65 Until`,
`SignalDaemonTests.cs:304 Until`, `McpReauthTests.cs:255 PollAsync`, `StopGenerationTests.cs:398 WaitForTurnsAsync` (a duplicate
of `ClientToolTests.cs:310`); also `FleetApiTests` `WaitForEventAsync` (not yet located). Names vary (`PollUntil`, `WaitUntil`,
`Until`, `PollAsync`, `WaitFor*`), so find helpers **by shape** (a loop with `Task.Delay` and a predicate/deadline), not by name.
Order: this plan merges **before** any other plan that edits these test files (see master plan).

## Context & constraints

- Helper lives in `tests/Agent42.Tests/TestWait.cs` (name it so; one static class), signature roughly
  `WaitUntilAsync(Func<bool> | Func<Task<bool>>, string what, TimeSpan? timeout = null)`; on timeout it throws a
  `TimeoutException` naming `what`. Default deadline generous (≥ 30 s: CI is slow) and measured with
  `Stopwatch`, not `DateTime.UtcNow`. Poll interval small (10 ms).
- **Negative assertions** ("nothing was sent") need a *positive barrier*, not a sleep: send a following message/marker that
  must be processed after the one under test and wait for **that**, then assert. If a test genuinely cannot get a barrier,
  keep a short delay but replace the magic number with a named constant and a comment saying what it bounds. Do not
  convert every delay: leave timeout bounds, hang simulators (`Delay(Infinite, ct)`) and fake latency alone.
- Production side (separate, optional step 5): 6 raw `Task.Delay` in `src/` with no `TimeProvider`
  (`SlackAdapter.cs:258`, `AgentApi.Fleet.cs:256,266`, `FleetTaskService.Run.cs:288`, `AgentMcpRunner.cs:153`,
  `HttpAgentWebhookSender.cs:73`). Only change one if a test of it currently needs a real wait; inject `TimeProvider` as
  the sibling sites do (`ChatGatewayService`, `ChatReplyStreamer`). Do not inject speculatively.
- Never change what a test asserts. If converting a sleep exposes a real race, that is a finding: stop, record it, and
  file a `fix/*` instead of papering over it.

## Steps

1. Create `TestWait.cs` + `TestWaitTests.cs` (succeeds early; times out with the `what` text; honours cancellation).
   Verify: `dotnet test --filter TestWaitTests`.
2. Replace the 8 private helpers + the inline loop with `TestWait`. One commit; run each touched test class. Parallelizable
   by file group, but they are small: do them in one pass.
3. Convert fixed-sleep-then-negative-assert sites (the two above first) to barrier-based waits.
4. Run the touched classes **20×** in a loop (`for i in 1..20; dotnet test --filter ...`) and report pass counts, to show
   flake rate did not rise.
5. (Optional) production `TimeProvider` injection, only where step 3 found a test that needs it.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Shared helper times out with a useful message | `WaitUntilAsync_TimesOut_NamingWhatItWaitedFor` | `TestWaitTests.cs` |
| Shared helper returns promptly when true | `WaitUntilAsync_ReturnsEarly` | `TestWaitTests.cs` |
| No private poll helper remains | `NoTestFile_DeclaresItsOwnPollHelper`: a source-text guard that detects **by shape** (a method whose body has a loop containing `Task.Delay` and a deadline/attempt bound, outside `TestWait.cs`) **and** by the name set `PollUntil|WaitUntil|Until|PollAsync|WaitFor\w*`; allowlist `LocalBridgeTestSupport` only if it delegates to `TestWait`. Prove the guard fails on a planted copy before relying on it (a regex that matches nothing is a vacuous test) | `ProjectLayoutTests.cs` |

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean.

## Out of scope

`FakeTimeProvider`; rewriting fakes' simulated latency; production behaviour changes.

## Review

(Critic pass on the diff: 2 HIGH, 2 MEDIUM, all HIGH/MEDIUM applied except finding 3, see below.)

## Implementation notes — where it diverged from the plan

- **`TestWait`** (`TestWait.cs`): `WaitUntilAsync(Func<bool> | Func<Task<bool>>, what, timeout, ct)` and `WaitForAsync<T>(produce, ready, what, ...)` for helpers that return the polled value. `what` is **optional** (default "the condition") instead of required: ~100 call sites already had no description, and a required string would have been 100 invented ones. New call sites should still pass it. The default deadline is 30 s everywhere (was 5-30 s), measured with `Stopwatch`.
- **Helpers that silently returned on timeout now throw** (`AgentGrainTests.PollUntilAsync`/`PollHistoryAsync`, the `Poll*Async` return-value helpers, `PollDeliveriesAsync`): a timeout is now a `TimeoutException` naming what was awaited instead of a later, unrelated assertion failure. No caller relied on a false predicate.
- **More copies than the plan listed.** The guard found ~20 inline poll loops the scout and critic missed (`Poll*Async` return-value helpers, `FinishedAsync`, `CrashObservedAsync`, `Finished`, several `for … Task.Delay` loops inside tests). All converted. `LocalBridgeTestHost.WaitUntilAsync` and `SignalDaemon.WaitForOpensAsync` were removed (callers use `TestWait`). `FleetApiTests.WaitForEventAsync` is an SSE reader, not a poll loop: renamed `ReadEventsUntilAsync` so the name guard stays strict.
- **Guard** (`ProjectLayoutTests.NoTestFile_DeclaresItsOwnPollHelper`): by name AND by shape (a `Task.Delay` anywhere under an enclosing `while`/`for`, climbing out of nested blocks to the method). Proven on planted copies (new name, delay directly under the loop, delay nested under an `if`, name-only). The allowlist is per **file + loop header** (a fake lease's blocking acquire, the streamer pacing loop, the fake-clock pump in `SignalDaemonTests`), not global.
- **Negative assertions.** Dropped messages (stranger, own echo/bot, group pairing, duplicate) are decided inline before `InjectAsync` returns, so their sleep was dead weight and is removed. The cases that act on the turn's worker (queued message while the gate is closed, `/stop` drop, files-only-unusable, voice failures) have no positive event to wait for and keep a **named** bound (`ChatTurnPipelineTests.NothingHappensGrace`, with the reason). A first draft removed the last two sleeps; the critic showed that narrowed what the tests detect, and they were restored.
- **Not done (critic finding 3, deliberate):** about 15 other `Task.Delay(<literal>)` sites followed by a negative assertion (`ChatGatewayServiceTests`, `SignalDaemonTests`, `AgentWebhookGrainTests`, `ChatDeliveryTests`, `FleetResultDeliveryTests`, `LocalBridgeKestrelTests`, `UsageIngestionServiceTests`, Slack/Telegram/Discord) keep their literal. The plan's Step 3 mandated only the pipeline sites; they are not poll loops, and none was observed flaky in the 20x runs. A follow-up can name them.
- `ChatReplyStreamerTests` delays are pacing for the streamer's real-time edit throttle (not waits): left alone.
- **Step 5 (production `TimeProvider`) not done:** no converted test needed one.
- **Step 4 evidence:** the 33 touched test classes + `CopilotAgentTests` ran 20 times: see the PR description for the pass count.

