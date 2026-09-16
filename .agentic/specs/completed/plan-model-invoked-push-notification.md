# Plan — a model-invoked push notification tool

planned-against: main @ 1c5f241

## Overview

Give the model a tool — `send_push_notification` — that delivers a Web Push notification, with a
**title and body the model writes**, to the user's subscribed devices, from **any** turn including
the live one. Today the only notification the app can raise is ADR 0028's fixed away-reply push
("Replied while you were away.", titled with the agent name) on the two async paths.

Why now: ADR 0028 shipped the whole transport (VAPID, subscription grain, sender, SW handlers) and its
Out-of-scope section closes with "no rich bodies/actions/images". Rich bodies are now wanted, and the
transport needs no change to deliver them.

**Do not cite 0028's "no notifying for interactive turns" here.** Its actual text is "no notifying for
interactive turns **the user abandons mid-stream**" — an ADR-0046-shaped carve-out about a different
scenario (a dead stream), not a general ban on live-path notification. The live-path decision below
stands on 0028's *service-worker* section instead, which already argues that a notification arriving
while the user is looking at the device is "acceptable and arguably wanted".

## Prerequisite — the branch-protection hook blocks this feature's own filenames

**Discovered at plan time; `/execute-plan` will hit this immediately.**
`.claude/hooks/protect-main.mjs:90-91` detects a commit/push with:

```js
const isPush = /\bgit\b[^\n;|&]*\bpush\b/.test(cmd);
```

`\b` treats `-` and `/` as word boundaries, so **any git command whose text contains the lowercase
token `push` anywhere — including in a path — is treated as a push.** While on `main`,
`protectedTargets` then matches and the command is blocked. Already reproduced:
`git status --porcelain .agentic/specs/plan-model-invoked-push-notification.md` was blocked.

This is **pre-existing**, not caused by this plan — `git add docs/features/push-notifications.md` and
`git add src/Agent42.Backend/wwwroot/js/push.js` are blocked on `main` today for the same reason. It
bites this feature hardest because **the branch name itself would contain `push`**, so
`git checkout -b feature/model-invoked-push-notification` is blocked from `main` and the branch cannot
be created. (Capitalised `Push` is unaffected — the regex is case-sensitive — so
`src/Agent42.Agents/Push/…` paths are fine.)

**Two ways out; the user chooses before execution starts:**
1. **Fix the hook** (recommended, own tiny MR first): require `push`/`commit` to be a
   whitespace-delimited token rather than a `\b`-delimited one —
   `/\bgit\b[^\n;|&]*\spush(?:\s|$)/`, and the same for `isCommit`. That still matches `git push` and
   `git -C dir push` while rejecting `…-push-notification.md` and `push.js`.
2. **Sidestep it** — name the branch `feature/model-invoked-notification-tool` (no lowercase `push`)
   and accept that individual `git` commands naming `push.js` or `push-notifications.md` must be run
   from the feature branch, where `protectedTargets` does not match.

Option 1 is a security-control change and must not be made silently; it is listed here as a decision,
not an action.

## Affected areas

Verified against the tree at `1c5f241`.

**New files**
- `src/Agent42.Agents/Push/AgentGrain.PushTool.cs` — the tool. A **sibling** of the existing
  `AgentGrain.Push.cs`, not an extension of it (see Context).
- `src/Agent42.Agents/Push/PushToolLimits.cs` — caps + the text sanitizer.
- `.agentic/decisions/0075-model-invoked-push-notification.md` — ADR (next free number; `0074` is
  the highest).

**Modified**
- `src/Agent42.Agents/Tools/ToolCatalog.cs` — new key, `AllSelectableToolKeys`, `Descriptors`,
  `ToolNameToKey`.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — `AIFunctionFactory.Create` + the
  unconditional `tools` list.
- `src/Agent42.Agents/Conversations/AgentGrain.cs` — the turn counter field, and its **two** reset
  sites.
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — `AgentGrainParts()` gains the new partial.
- `tests/Agent42.Tests/ToolSelectionApiTests.cs` — **will go red without an edit.** It asserts the
  exact `/api/tools` key set with `Assert.Equal(new[] { "web_search", …, "read_web_document",
  "skill:scheduledprompt", "skill:websearch" }.Order(), keys.Order())`. Add the new key.
- `tests/Agent42.Tests/FakeChatClient.cs` — trigger phrases for the new tool.
- `tests/Agent42.Tests/AgentGrainTests.cs` — new tests; one rename (see AC2).
- `.agentic/decisions/0028-web-push-when-agent-replies-while-away.md` — an **inline "Amended by
  [ADR 0075]" banner**, per the convention `0021`/`0026` follow. A gotcha amendment does not reach
  ADR 0028's own Decision text, and one sentence there becomes false: "A user actively chatting
  receives their reply in-stream; no push is generated for that turn."
- `tools/Agent42.DevHost/CannedChatClient.cs` — offline trigger.
- `.agentic/gotchas.md` — amend the ADR 0028 entry; add one new entry.
- `docs/features/push-notifications.md`, `docs/features/tool-selection.md`, `CLAUDE.md`.

**Deliberately NOT modified** — verified, not assumed:
- `src/Agent42.Backend/wwwroot/**` — the SW already reads `title`/`body`/`conversationId` from the
  payload and sets **no `tag`**; the picker is fully data-driven off `/api/tools`, and its "Built-in"
  group is everything that is *not* a `skill:` or `mcp:` row, so a new static key renders there with
  no JS change. **Therefore no `sw.js` `CACHE` bump is due** — the bump rule is keyed on `wwwroot/js/`
  changing. (Do not restate the MCP partition rule as a `startsWith("mcp:")` test: ADR 0058's rule is
  to partition on `ToolDescriptor.ParentKey`, because `startsWith` matches both server and tool rows.
  It does not affect this change, but repeating it wrongly in a handoff doc is a drift source.)
- `IPushNotificationSender` / `PushMessage` / `WebPushNotificationSender` / `UserPushGrain` — no
  signature change, so the four host registrations and `RecordingPushNotificationSender` are untouched.
- `src/Agent42.Backend/Api/AgentApi.cs` — no new route, so the `63` route-count comment stands.
- `src/Agent42.Agents/Telemetry/UsageSpanProjector.cs` — verified: it whitelists by **operation name**
  (`chat` / `execute_tool`), never per tool, so ADR 0031's tool-calls-by-name table picks the new tool
  up with no change.
- No new secret, Cosmos container, blob container, grain, folder, `excludedPaths` entry, Bicep change,
  package, or `[Id(n)]` — **and therefore no migration**.

## Context & constraints

### Decisions the user made (do not re-litigate)

1. **Allowed on every path, including the live `ChatCoreAsync` turn.** This is a deliberate amendment
   to ADR 0028's load-bearing "never push from `ChatCoreAsync`" rule.
2. **The model writes both title and body**, budget-capped and sanitized.
3. **Tap deep-links to the calling conversation** via the existing `PushMessage.ConversationId`.
4. **Cap: 3 pushes per turn.**
5. **REFUSED in incognito chats** (ADR 0034) — the tool returns a refusal when `_ephemeral`.
6. **The away-reply push is suppressed when the tool already pushed this turn.**

Decisions 5 and 6 were both **reversed after the critic pass**; the reasoning is below, and the
rejected first answers are in the Review section so the reversal is not silently re-reversed.

### The ADR 0028 rule is NARROWED, not repealed — and this is the easiest thing to get wrong

`.agentic/gotchas.md` → *"The away-reply push belongs in the two async callers, never in
`ChatCoreAsync`"* must be amended to say the rule governs **`SendAwayReplyPushAsync` specifically**,
and that a model-invoked push is a different thing that may fire from any path. The existing rule —
including its two placement traps (`EnqueuePromptAsync`'s `_modelId` gate; `ReceiveReminder`'s
post-`WriteStateAsync` position) — **must not read as repealed**. The whole reason the tool goes in a
**new partial** rather than into `Push/AgentGrain.Push.cs` is that the file split is itself the
documentation that these are two rules, not one.

### The reset sites are TWO, not three, and not one — this is the trap

`ChatAsync` and `EnqueuePromptAsync` both drain `ChatCoreAsync`. **`ReceiveReminder` does not** — it
calls `_agent.RunAsync` directly (`src/Agent42.Agents/Conversations/AgentGrain.cs` says so at the
ADR 0066 "consume site 2 of 2" comment, and that ADR was bitten by exactly this).

So the counter resets in **two** places:
- the top of `ChatCoreAsync` (covers the live turn *and* the agent-run drain), and
- `ReceiveReminder`'s existing reset block, beside `_turnLocation` / `_ephemeral` /
  `_webDocumentIngested`.

This is deliberately **not** the `_ephemeral` shape of "set it in all three entrypoints". Those fields
carry a *different value* per path, so each caller must set them; this counter's reset value is always
`0`, so the shared core is the better site and a future `ChatCoreAsync` caller cannot forget it.

**There is no fourth path — verified at plan time, so do not re-derive it, but do not weaken it
either.** The two other things in the grain that run a model were checked:
`GenerateFollowUpsAsync` (ADR 0036) builds a bare `ChatOptions` carrying **no `Tools`**, and
`RecordImageTurnAsync` (ADR 0020) runs a `ChatClientAgent` over `ImageGenerationChatClient` whose
options carry **no `Tools`** and which can only return the contents it was constructed with. Neither
can invoke this tool. ADR 0070's `ask_agent` is not a fourth path either — it calls `ChatAsync(…,
interactive: false)`, so it drains `ChatCoreAsync` and is covered by reset site 1.

**Verify during implementation:** `ChatCoreAsync` is an `IAsyncEnumerable` iterator, so code before
the first `yield` does not run until enumeration begins. Both callers enumerate immediately, so this
is fine — but confirm it rather than assume it, and put the reset above any `yield`.

### Incognito: refuse, and do not "improve" it into an allow

`.agentic/decisions/0034-ephemeral-chat.md` refuses scheduling because a reminder "persists the
session **and sends the away-reply Web Push (ADR 0028)** — **both** break the guarantee." ADR 0034
therefore names a Web Push, by itself, as guarantee-breaking. Two concrete reasons it is right:

- The notification's title and body are a **durable, plaintext, at-rest artifact** in the OS
  notification shade, derived from a conversation that promised to leave no trace. That is a *new
  category* of leak for ADR 0034, not an inherited ADR 0028 residual.
- An incognito chat has **no sidebar row**, so the notification would become the *only* handle to it,
  on any device — the opposite of "not listed".

So the tool refuses when `_ephemeral`, using the shape `SchedulePromptAsync` and `read_web_document`
already use. Because this **upholds** ADR 0034 rather than amending it, ADR 0075 cites 0034 and adds
no banner to it. An earlier draft allowed it by analogy to "images persist — accepted"; that was the
wrong precedent (0034 singles push out by name) and it was rejected.

### The away-reply push is suppressed when the tool already pushed

On the two async paths the model may call the tool **and** `SendAwayReplyPushAsync` still fires
afterwards. With no `tag`, those stack as two lock-screen entries — the model's "Build finished"
followed by the generic "Replied while you were away." So both call sites gain
`&& _pushesThisTurn == 0`.

**This is a behaviour change on ADR 0028's own paths**, which is the main reason 0028 needs the
inline amendment banner and not just a gotcha edit. Add the new condition to the *existing* guard
expressions without disturbing them: `EnqueuePromptAsync` keeps its `_modelId` gate, and
`ReceiveReminder` keeps its post-`WriteStateAsync` position and its defensive `try`. Both of those
traps are in the ADR 0028 gotcha and neither is being relaxed.

### Why the tool reads `IUserPushGrain` itself

`IPushNotificationSender.SendAsync` returns `Task`, **never throws**, and early-returns silently when
the user has zero subscriptions. So the sender can tell the tool nothing. Rather than widen that
contract across four host registrations, the tool does its own
`GrainFactory.GetGrain<IUserPushGrain>(_userId).GetAllAsync()` (the
`GrainFactory.GetGrain<IUserProfileGrain>` shape already in `AgentGrain.cs`) to answer the one
user-visible question — *are any devices subscribed?* — and skips the send when none are.

**This does not make the tool able to report delivery, and it must not pretend to.** Whether a push
actually arrived stays unknowable by design (ADR 0028's best-effort/prune contract). The return string
and the tool description must both say "queued to N device(s)", never "delivered".

### Sanitization: an allowlist would be WRONG here, and that needs saying out loud

`WebDocumentLimits.SanitizeFileName` uses a strict ASCII allowlist because its sink is the **system
prompt** and a filename. This sink is different: `showNotification(title, {body})` renders **plain
text** — there is no HTML parse and no prompt. An ASCII allowlist would mangle ordinary Dutch text and
every emoji, for no security gain.

The rule here is therefore: **strip Unicode control and format characters** (notably the bidi
overrides such as U+202E, which really can spoof notification text), collapse whitespace runs
(newlines included — they fake structure in a notification), hard-cap the length, and **reject when
nothing usable remains**. The divergence from ADR 0066's "allowlist, never a denylist" must be stated
in the ADR with this justification, or it reads as a regression.

### The security consequence to record honestly

The notification text is **model-authored**, so every inbound prompt-injection channel in the app —
ADR 0011 (web search), 0024/0067 (MCP results), 0027 (followed links), 0049 (uploaded `SKILL.md`),
0066 (web documents) — can now put **attacker-chosen text on the user's lock screen**. Sanitization
bounds the characters, not the meaning; "Your bank needs you to call 555-0100" sanitizes perfectly.

Sharper, and easy to miss: **this is reachable from the two key-holder endpoints.** A holder of an
ADR 0022 `/api/agent-run` key, or an ADR 0070 `/api/agent-mcp` key, can already cause the fixed
away-reply push to fire — but with this tool they can cause a push whose **text they control**, to the
*owner's* devices. That is a real escalation of both endpoints and belongs in the ADR, not only here.

### No `SKILL.md`, deliberately

ADR 0067's headline lesson is that **the rule the model actually reads is in the tool descriptions**,
not `SKILL.md`. Adding a skill would also mint a `skill:{name}` picker row that lands **OFF for anyone
who ever pressed Save** (ADR 0048's consequence) — a second silent-off key for one feature. All the
guidance this tool needs (when to use it, don't spam, don't claim delivery, the cap) fits in the
`AIFunctionFactory` description and the two `[Description]` parameter attributes. Put it there.

### Governing documents to read before starting

- `.agentic/decisions/0028-web-push-when-agent-replies-while-away.md` — especially the "SW always
  shows the notification" section, which already argues a notification arriving while the user is
  looking at the device is "acceptable and arguably wanted"; that is the ground the live-path decision
  stands on.
- `.agentic/decisions/0021` (selectable tools), `0034` (incognito), `0067` (tool-description lesson),
  `0066` (`WebDocumentLimits` as the sanitizer exemplar and the fetch-path precedent).
- `.agentic/gotchas.md` — the ADR 0028 entry, the `null == all` selectable-tools entry, the
  ephemeral-chat entry, and the "tool return value is persisted into the 2 MB session" entry.
- Exemplars to copy the shape of: `src/Agent42.Agents/Location/AgentGrain.LocationTool.cs` (a tool
  partial that reads a turn-scoped field and returns a plain-language message when it cannot act) and
  `src/Agent42.Agents/Location/GeoToolLimits.cs` (a small limits class).

### Naming (settled, so the executor does not re-decide)

| Thing | Value |
|---|---|
| Catalog key | `push_notification` (const `ToolCatalog.PushNotification`) |
| Tool name | `send_push_notification` |
| Picker label | `Push notifications` |
| Partial | `src/Agent42.Agents/Push/AgentGrain.PushTool.cs` |
| Limits | `PushToolLimits` — `MaxTitleLength`, `MaxBodyLength`, `MaxPushesPerTurn = 3` |

The key and tool name deliberately differ, unlike `read_web_document` where a one-tool key is spelled
identically to its tool. The reason: the key is a **consent noun** rendered in the picker
("Push notifications" — the thing you are switching on), while the tool name is an **imperative** the
model reads. `send_push_notification` as a checkbox key would read as a verb in a list of nouns, and
`push_notification` as a tool name invites the model to treat it as a topic rather than an action.
`location`/`scheduling` already establish that a key need not equal a tool name.

## Steps

Steps 1–3 are sequential (each depends on the last compiling). Steps 4–6 are **parallelizable** once
3 is green. Step 7 is last.

### Step 1 — `PushToolLimits` + the sanitizer
**Files:** `src/Agent42.Agents/Push/PushToolLimits.cs` (new)

Caps (`MaxTitleLength = 100`, `MaxBodyLength = 300`, `MaxPushesPerTurn = 3`) and a
`SanitizeNotificationText(string, int maxLength)` returning the cleaned string (empty when nothing
usable survives). Document *why* it is not an allowlist, per Context.

**Verify:** `dotnet build Agent42.slnx`, plus the Step 4 unit tests when they land.

### Step 2 — the turn counter and its two reset sites
**Files:** `src/Agent42.Agents/Conversations/AgentGrain.cs`

Add `private int _pushesThisTurn;` beside the other turn-scoped fields. Reset to `0` at the top of
`ChatCoreAsync` (above any `yield`) and in `ReceiveReminder`'s existing reset block. Extend the
comments at both sites to say why there are exactly two, naming the fact that `ReceiveReminder` does
not drain `ChatCoreAsync`.

Then add `&& _pushesThisTurn == 0` to **both** `SendAwayReplyPushAsync` call sites (decision 6),
leaving `EnqueuePromptAsync`'s `_modelId` gate and `ReceiveReminder`'s post-`WriteStateAsync`
placement and defensive `try` exactly as they are.

**Verify:** `dotnet build Agent42.slnx`; `dotnet test Agent42.slnx --filter AgentGrainTests` stays green.

### Step 3 — the tool, and its catalog wiring
**Files:** `src/Agent42.Agents/Push/AgentGrain.PushTool.cs` (new),
`src/Agent42.Agents/Tools/ToolCatalog.cs`, `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`,
`tests/Agent42.Tests/ProjectLayoutTests.cs`

The tool: two `[Description]`-annotated `string` parameters (`title`, `body`) plus a
`CancellationToken` (which `AIFunctionFactory` keeps out of the JSON schema — see the
`GetCurrentLocationAsync` comment). Order of checks:

1. `_ephemeral` → refuse, no send (decision 5);
2. over `MaxPushesPerTurn` → refuse, no send;
3. sanitize both; either empty → refuse, no send;
4. read subscriptions; none → tell the model plainly, no send;
5. `_pushesThisTurn++`, send via `_pushSender.SendAsync(_userId, new PushMessage(title, body,
   ConversationIdFromGrainKey(...)))`, return "queued to N device(s)".

**The counter increments only on an actual send** (step 5), so a refused call does not consume budget.

Log that it ran and the device count — **never the title or body** (the `GetCurrentLocationAsync`
logging rule: this repo stamps user identity on every span, ADR 0010).

Catalog: append the key **last** in `AllSelectableToolKeys` and in `Descriptors` (with a
`Description`, which `EveryStaticDescriptor_HasADescription` requires), and add
`["send_push_notification"] = PushNotification` to `ToolNameToKey` (which
`EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool` requires). Register it in
`BuildAgent`'s **unconditional** `tools` list — like `readWebDocument`, it is not part of ADR 0015's
hosted-tools swap, so it must be present on both capability branches.

Add `"Push/AgentGrain.PushTool.cs"` to `ProjectLayoutTests.AgentGrainParts()`.

Also update the hardcoded key list in `tests/Agent42.Tests/ToolSelectionApiTests.cs`.

**Verify:** `dotnet test Agent42.slnx --filter "ProjectLayoutTests|ToolCatalogTests|ToolSelectionApiTests"`.

### Step 4 — behavioural tests (parallelizable)
**Files:** `tests/Agent42.Tests/FakeChatClient.cs`, `tests/Agent42.Tests/AgentGrainTests.cs`

Add trigger phrases.

**AC4 needs a mechanism `FakeChatClient` does not have, and the earlier "copy `BuildSeedingChainCall`"
instruction was wrong — ignore it.** That builder gates on a `HashSet<string>` of tool *names*
(`calledThisTurn`), so it structurally cannot emit the same tool twice, let alone four times. Emitting
four calls of one tool needs two things it has none of:
- **count** occurrences of `send_push_notification` in the messages after `lastUserIndex`, rather than
  testing set membership; and
- a **distinct `CallId` per call** (every existing builder hardcodes one, e.g. `"fetch-call-1"`), or
  call/result matching misbehaves.

Build that counting branch deliberately, gate it behind its own trigger phrase, and keep it clear of
the existing membership guard.

Rename `ChatAsync_DoesNotSendPush` → `ChatAsync_DoesNotSendAwayReplyPush` and comment it to say it
pins the *away-reply* push's absence, which is still true. Its old name becomes a lie once this ships.

**Two harness traps, both verified:** (a) `UserIdFromGrainKey` returns the **whole grain key** when it
contains no `/`, and `AgentGrainTests` conversation keys are slash-free — so seeding `IUserPushGrain`
must use the full conversation key as the userId. (b) `SharedClusterFixture.ResetSiloState` resets
`RecordingPushNotificationSender` but **not** `IUserPushGrain`, whose Orleans state survives across
tests in the class-shared cluster — so AC7's "no subscriptions" test is only safe with a key unique to
that test. Do not rely on a reset that does not happen.

**Verify:** `dotnet test Agent42.slnx --filter AgentGrainTests`.

### Step 5 — sanitizer unit tests + the AC14 source guard (parallelizable)
**Files:** `tests/Agent42.Tests/PushToolLimitsTests.cs` (new)

Holds both the `SanitizeNotificationText` cases and AC14's source-text guard over
`src/Agent42.Agents/Push/AgentGrain.PushTool.cs`.

**Verify:** `dotnet test Agent42.slnx --filter PushToolLimitsTests`.

### Step 6 — DevHost offline leg (parallelizable)
**Files:** `tools/Agent42.DevHost/CannedChatClient.cs`

A trigger that calls `send_push_notification`.

**The obvious version of this leg only ever exercises one branch, and that must be avoided.** DevHost
registers `NullPushNotificationSender` **and** `new PushConfig(Enabled: false, …)`, so the UI hides
the subscribe toggle and `IUserPushGrain` stays empty — check 4 ("no subscriptions") fires before the
counter ever increments, and the cap can never be reached. The way through: **`POST
/api/push/subscriptions` is not gated on `PushConfig.Enabled`**, so seed a fake subscription by curl
first, then drive the triggers. Write that curl into the step when executing.

Honest limit either way: with the null sender no notification is ever produced, so this leg proves the
tool is **registered, gated, capped and returns the right strings** — never delivery. Real delivery
stays a manual Android-device check, exactly as ADR 0028 recorded for its own frontend leg.

**Verify:** `dotnet run --project tools/Agent42.DevHost`, seed a subscription, drive the triggers
(normal, over-cap, incognito), read the tool chips.

### Step 7 — docs, gotchas, ADR
**Files:** `.agentic/decisions/0075-*.md`, `.agentic/gotchas.md`,
`docs/features/push-notifications.md`, `docs/features/tool-selection.md`, `CLAUDE.md`

- ADR 0075 — **amends 0028** (both the live-path rule and the new away-reply suppression), extends
  0021, cites 0034. It records: the injection consequence; the `/api/agent-run` + `/api/agent-mcp`
  escalation; the incognito **refusal** and why 0034 required it; the away-reply suppression; the
  no-`SKILL.md` call; the sanitizer divergence from 0066; and the lands-OFF-after-Save property.
  It must also **restate ADR 0028's egress paragraph**, not merely cite it: 0028 reasoned that the
  payload is RFC 8291-encrypted and that only "the title is the user-chosen agent name". Title *and*
  body are now model-authored, conversation-derived text — still end-to-end encrypted in transit, but
  the sentence describing what is in there no longer holds.
  One line worth recording because it is expensive to rediscover: ADR 0070's `AgentMcpRunner` abandons
  the enumerator on timeout, which loses the turn — but a push already fired by the tool **survives**
  that, so the user can get a notification deep-linking to a conversation with no messages.
- Add the **"Amended by ADR 0075"** banner to `.agentic/decisions/0028-*.md` itself (M3).
- Gotchas — amend the ADR 0028 entry per Context, **including its Evidence line, which names
  `ChatAsync_DoesNotSendPush`** (renamed in Step 4); add one new entry for the two-reset-sites rule.
- `docs/features/tool-selection.md:9-10` lists the curated keys and is **already stale** (it omits
  `read_web_document`, ADR 0066). Since this step edits that exact line, fix both in passing and say
  so in the MR.

**Verify:** `node .agentic/scripts/check-doc-paths.mjs` (or the CI doc-path job), `dotnet test Agent42.slnx`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | The model can push from a **live** `ChatAsync` turn, with its own title and body | `ChatAsync_ModelInvokedTool_SendsPush` | `AgentGrainTests.cs` |
| AC2 | The **away-reply** push still does not fire on a live turn | `ChatAsync_DoesNotSendAwayReplyPush` (renamed) | `AgentGrainTests.cs` |
| AC3 | Existing async away-reply pushes are unchanged | `ReceiveReminder_SendsPush_WithAgentName`, `EnqueuePrompt_SendsPush`, `EnqueuePrompt_NoModel_DoesNotSendPush` (unchanged) | `AgentGrainTests.cs` |
| AC4 | A 4th push in one turn is refused and not sent | `PushTool_CapsPushesPerTurn` | `AgentGrainTests.cs` |
| AC5 | The counter resets between turns (live path) | `PushTool_CounterResetsOnTheNextTurn` | `AgentGrainTests.cs` |
| AC6 | The counter resets on the **reminder** path — the second reset site | `PushTool_CounterResetsOnTheReminderPath` | `AgentGrainTests.cs` |
| AC7 | With no subscriptions the model is told plainly and **no send occurs** | `PushTool_NoSubscriptions_TellsTheModelAndSendsNothing` | `AgentGrainTests.cs` |
| AC8 | Control/format chars stripped, bidi override removed, length capped | `Sanitize_StripsControlAndFormatCharacters`, `Sanitize_CapsLength` | `PushToolLimitsTests.cs` |
| AC9 | Title or body empty after sanitizing → refused, no send | `PushTool_EmptyAfterSanitize_SendsNothing` | `AgentGrainTests.cs` |
| AC10 | An incognito chat **refuses** the tool and sends nothing (ADR 0034) | `PushTool_EphemeralChat_RefusesAndSendsNothing` | `AgentGrainTests.cs` |
| AC11 | Deselecting `push_notification` removes the tool; selecting keeps it | `PushTool_IsGatedByToolSelection` | `AgentGrainTests.cs` |
| AC15 | When the tool pushed this turn, the away-reply push is **suppressed** on both async paths | `ReceiveReminder_SuppressesAwayReplyPush_WhenToolAlreadyPushed`, `EnqueuePrompt_SuppressesAwayReplyPush_WhenToolAlreadyPushed` | `AgentGrainTests.cs` |
| AC16 | A stored (non-null) tool selection made before this ships does **not** enable the new key | `LegacyAgentSnapshot_DoesNotEnablePushNotification` — the `LegacyAgentSnapshot_DoesNotEnableMemory` shape (ADR 0057) | `AgentGrainTests.cs` |
| AC17 | `/api/tools` exposes the new key with its description | `ToolSelectionApiTests` (existing, key list updated) | `ToolSelectionApiTests.cs` |
| AC12 | Catalog wiring: key present, described, name-mapped | `EveryStaticDescriptor_HasADescription`, `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool` (both existing, now covering the new key) | `ToolCatalogTests.cs`, `AgentGrainTests.cs` |
| AC13 | The new partial is pinned by the layout guard | `ProjectLayoutTests` (`AgentGrainParts`) | `ProjectLayoutTests.cs` |
| AC14 | The tool never logs the notification text | `PushTool_DoesNotLogNotificationText` — a **source-text** guard, not behavioural | `PushToolLimitsTests.cs` |

**AC14 is a source-text guard because there is no log-capture seam in `tests/`** (checked: no
`ILoggerProvider` / fake-logger implementation exists). The precedent for reading C# source in a test
is `tests/Agent42.Tests/McpUrlSeedingTests.cs`. Scope the read to
`src/Agent42.Agents/Push/AgentGrain.PushTool.cs` and assert the `_logger` call's argument list does
not carry the title or body parameters — **and mutation-check it**, because ADR 0072's lesson is that
a loosely-anchored source-text assertion goes *vacuous rather than red*. If it cannot be made
non-vacuous cheaply, drop the test and keep the rule as a comment — which is exactly how
`GetCurrentLocationAsync` carries the identical "never log the coordinates" rule today.

**AC6 is the load-bearing one.** It is the ADR 0012 `_turnLocation` bug shape: a counter reset only in
`ChatCoreAsync` leaves the reminder path inheriting the previous turn's count, so a repeating reminder
would go silent after three pushes for the life of the activation. Mutation-check it — remove the
`ReceiveReminder` reset and confirm AC6 alone goes red.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura ∩ `git diff -U0` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at **build time**, severity `error` — a method over it fails `dotnet build` |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |

Enforcement: local `soft_warning`, CI `hard_block`.

**Complexity watch:** the tool's four sequential guard clauses plus the send is the kind of method
that drifts toward 15. If it trips, the fix is to lift sanitize-and-validate into `PushToolLimits`,
**never** a ternary that buys the metric and costs the reader.

Branch: **`feature/model-invoked-notification-tool`** (hard rule 1 — never commit to `main`).
The name deliberately contains no lowercase `push`; see the Prerequisite section. If the hook is fixed
first (option 1 there), any name is fine — but this one is safe either way, so prefer it.

## Out of scope

- **No `tag` on the notification.** The SW sets none today, so notifications stack rather than
  coalesce. The per-turn cap is the bound; adding a `tag` would change ADR 0028's away-reply
  behaviour too and belongs in its own change.
- No delivery receipts, no notification actions/buttons, no images, no per-user rate limit across
  turns, no quiet hours.
- No iOS support (ADR 0028's Android/Chromium scope is unchanged).
- No change to who may subscribe, or to the subscription UI.
- No encryption of stored subscriptions (still deferred, ADR 0028).
- No `SKILL.md` — see Context.

## Review

A `critic` pass ran against the pre-review draft. Verdict: **not ready to approve** — 1 Critical,
6 Major, 9 Minor. All were accepted and fixed; **nothing was rejected**. Two findings changed a user
decision and were taken back to the user before revising.

### Accepted and fixed

| # | Finding | How it was handled |
|---|---|---|
| C1 | The incognito **allow** contradicted ADR 0034, which names a Web Push by itself as guarantee-breaking; the recorded "dead deep link" residual was also backwards (the chat is live while activated, and the notification text is a *new* at-rest leak) | Re-asked the user with the correction. **Decision reversed to refuse.** New Context section; AC10 inverted |
| M1 | `ToolSelectionApiTests.cs` asserts the exact `/api/tools` key set and would go red; the file was in no step | Added to Affected areas, Step 3 and AC17 |
| M2 | `FakeChatClient` structurally cannot emit one tool four times (`calledThisTurn` is a name **set**), and every builder hardcodes one `CallId` — so "copy `BuildSeedingChainCall`" was actively misleading | Step 4 rewritten: the old instruction is explicitly retracted and the counting-plus-distinct-CallId mechanism is spelled out |
| M3 | Amended ADRs were not in Modified; a gotcha edit cannot reach ADR 0028's own Decision text | ADR 0028 added to Modified with the inline "Amended by" banner required by the 0021/0026 convention |
| M4 | Two notifications per async turn was undecided and user-visible | Re-asked the user. **Away-reply push is now suppressed when the tool pushed** (decision 6); new Context section, Step 2 work, AC15 |
| M5 | The branch name in the gate section was the one the Prerequisite says cannot be created | Branch is now `feature/model-invoked-notification-tool` |
| M6 | DevHost's `PushConfig(Enabled: false)` + null sender means the zero-subscription branch fires first, so the leg could never reach "gated, capped" | Step 6 rewritten with the curl seed (`POST /api/push/subscriptions` is not gated on `Enabled`) and an honest statement of what the leg does and does not prove |
| m1 | ADR 0028 misquoted — its carve-out is "interactive turns **the user abandons mid-stream**" | Overview corrected and the correct supporting section (0028's service-worker argument) cited instead |
| m2 | 0028's egress paragraph reasons only about the agent-name title | ADR 0075 must restate it, not cite it |
| m3 | The picker-partition rule was restated in the `startsWith("mcp:")` form ADR 0058 warns against | Reworded, with the warning kept |
| m4 | Key/tool-name divergence was billed as "settled" with no justification | Justification added (consent noun vs. imperative) |
| m5 | AC14's test file had no owning step | Step 5 retitled to own it |
| m6 | The lands-OFF-after-Save property was prose only | AC16, reusing ADR 0057's `LegacyAgentSnapshot_DoesNotEnableMemory` shape |
| m7 | Two harness traps: `UserIdFromGrainKey` returns the whole key when slash-free; `ResetSiloState` does not reset `IUserPushGrain` | Both written into Step 4 |
| m8 | The ADR 0028 gotcha's Evidence line names the test being renamed | Called out in Step 7 |
| m9 | A push fired by the tool survives ADR 0070's enumerator abandonment, deep-linking to an empty conversation | Recorded in the ADR outline |

### Confirmed by the critic, not changed

- The **two-reset-site** design holds, and there is **no fourth path** — independently verified against
  `AgentGrain.cs:1063`/`:1085-1090`, `FollowUps.cs:46-53`, `ImageTurn.cs:167-182`,
  `AgentMcpRunner.cs:131`.
- The SW renders **plain text** (`sw.js:128-133`, no `innerHTML`, no `tag`), so the anti-allowlist
  argument stands on evidence.
- `UsageSpanProjector` is operation-keyed; `Sanitize_OrdersStaticFirst_*` and `AllKeysExpanded()` are
  safe under append-last; `ExpectedFolders()` is untouched; the gate thresholds match the yaml.
- The branch-protection Prerequisite is real.

### Rejected

None.

---

## Implementation notes — where execution diverged from this plan

Executed 2026-09-16 on `feature/model-invoked-push-notification`, commit `48db4dc`, against
`main @ 1c5f241` (the `planned-against` ref exactly — no drift). Full suite **1411 passed / 0 failed**,
`dotnet format` clean, new-code coverage **100% (99/99)**.

### 1. The Prerequisite was resolved a third way, and the branch name differs

The plan offered two ways past the `protect-main.mjs` false positive (fix the hook, or sidestep with a
branch name containing no lowercase `push`). Neither was taken. **The user was asked and chose to leave
the hook alone**, and the branch was created through the **PowerShell tool**, which the hook does not
match (`.claude/settings.json` registers it for `Bash` only). Once off `main`, `protectedTargets` matches
nothing, so every later command — including ones naming `push.js` or this branch — runs unblocked.

So the branch is **`feature/model-invoked-push-notification`** (the spec slug), not the plan's
`feature/model-invoked-notification-tool`; the user chose that too. **The hook bug is still live** for
anyone starting from `main`, and is deliberately unfixed here rather than folded into a feature MR.

### 2. The complexity gate fired, on a file the plan never mentioned

Adding the push-cap branch took `FakeChatClient.SelectToolCall` to **cognitive complexity 17 against the
15 threshold**, failing `dotnet build` outright — exactly the "expect to meet it" warning in CLAUDE.md.
Fixed the way the plan prescribes (lift, never a ternary): a new **`TryChainedToolCall`** helper now holds
**all four** scenarios that deliberately span more than one call in a turn — the pre-existing exfiltration
and two URL-seeding chains, plus the new push cap. Its `bool`/`out` shape is load-bearing: "handled, and
the answer is no further call" (what the exfiltration chain returns once the fetch has been attempted) has
to stay distinguishable from "not one of my scenarios", which a bare nullable return cannot express.
Behaviour and ordering are unchanged; the whole suite is the evidence.

### 3. Step 6 needed more than "a trigger"

The plan said DevHost needed "a trigger that calls `send_push_notification`". It needed three things:
a single-call trigger, a **counting branch with distinct CallIds** (the same mechanism as the test fake —
`CannedChatClient` has the same one-call-per-turn guard), that guard **widened to let the cap trigger
through** (it previously admitted only the seeding chain), and a `using Agent42.Agents;` for
`PushToolLimits`.

The plan's curl premise held exactly: `POST /api/push/subscriptions` returned **204 while
`GET /api/push/config` reported `enabled:false`**, so seeding a device offline works.

### 4. A harness trap worth keeping: `schedule_prompt` has no optional parameters

`BuildPushReminderCall` first omitted `repeatEveryMinutes`. That parameter is a **non-nullable `double`**,
so the call fails as a flat `"Error: Function failed."`, **nothing is scheduled, and the reminder test
fails much later with an empty send list** — a failure that looks like the feature rather than the fake.
A repeating reminder is also the case that matters, since that is what a missing counter reset silences.

### 5. Additions beyond the plan's AC table

- `ToolSelectionApiTests` also asserts the new key's **description** and `requiresHostedTools: false`
  (AC17), mirroring the ADR 0066 block beside it, rather than only extending the key list.
- `PushToolLimitsTests` carries four cases the plan did not enumerate: the cap **counts survivors, not
  arrivals** (so zero-width padding cannot starve a title), a surrogate pair is **never split**, accented
  text and emoji **survive** (the test that says no to "fixing" the denylist into an allowlist), and the
  bidi override is asserted on its own so a regression stripping only C0 controls cannot hide.
- AC14 was **kept, not dropped**: the source guard is scoped to the file's single `_logger` call, asserts
  on that call's argument list, and was **mutation-checked** (adding `cleanTitle` to the call fails it).
- AC6 was **mutation-checked** as the plan required: deleting the `ReceiveReminder` reset fails
  `PushTool_CounterResetsOnTheReminderPath` **and nothing else**.

### 6. One offline observation the plan did not anticipate

DevHost's `CannedChatClient` does **not** consult `options.Tools` before emitting a call, unlike
`FakeChatClient` (which returns null on a no-tools model). So with `push_notification` deselected the
offline leg shows a tool **call** whose result is the framework's
`Error: Requested function "send_push_notification" not found.` That is a fake artifact rather than a
product behaviour — and it is what makes the gate visible offline. Left as-is: changing the canned client's
dispatch contract is out of scope for this plan.

### 7. Process note

Steps 4–6 are marked parallelizable. They were run **sequentially by the main session** instead: all three
write into the same checkout, which CLAUDE.md permits only under worktree isolation, and two small test
files plus one DevHost run did not justify that setup. Files were written LF and normalized to CRLF before
linting — `dotnet format` reports a whitespace error on *untouched* lines when a file's endings change,
which reads like a formatting bug in code you did not write.

### 8. Critic round — findings accepted after the first commit

A `critic` pass ran on commit `48db4dc`. It cleared the `TryChainedToolCall` refactor as exactly
behaviour-preserving (ordering, and the null-vs-not-handled distinction, both verified independently).
**4 Major + 5 Minor, all accepted; none rejected.** Two were real test-quality gaps that would have
shipped guards which do not guard:

- **M3 — the sanitizer's WIRING was untested.** Every behavioural test sent text that survives sanitizing
  byte-identically, so an implementation that computed the clean strings only for the emptiness check and
  then passed the **raw** title and body into `PushMessage` would have passed the entire suite — while
  putting a U+202E override and a 500-character body on a lock screen. Added
  `PushTool_SendsTheSanitizedText` + a `BuildDirtyPushCall` fake; **mutation-checked** (sending raw text
  fails that test and nothing else). This is the security control of the feature.
- **M2 — AC15's agent-run half was racy in the false-pass direction.** The tool's push is recorded *during*
  the drain and the away-reply push *after* it, so `PollUntilAsync(count >= 1)` + `Assert.Single` returned
  before the second send could land: deleting the suppression would frequently still have been green. Fixed
  by awaiting a further grain call — `AgentGrain` is **non-reentrant**, so it queues behind the OneWay turn
  and returns only once the post-drain call site has run. Mutation-checked **3/3** runs.
- **M1 — `IPushNotificationSender`'s own doc comment was false** ("never called from the live interactive
  stream"), as were `PushMessage`'s param docs ("the agent's name", "a short generic body line"). The plan's
  "Deliberately NOT modified" list cleared that file on the grounds of *no signature change*, which is true
  and was not the relevant test. Both rewritten to carry the two-senders-two-rules distinction.
- **M4 — the `/api/agent-mcp` escalation had an obvious mitigation the plan never considered.** ADR 0070
  deliberately made that surface silent, and `null == all` would have reversed that decision **by default**.
  **The user was asked and chose to exclude it**: `WithoutMemory` → `WithoutMemoryOrPush`. This is a scope
  addition beyond the approved plan, taken because it preserves a recorded decision in another ADR rather
  than eroding it silently. `/api/agent-run` deliberately keeps the tool.
- **m1/m2/m5** — the turn-scoped-field-family gotcha now says why `_pushesThisTurn` is *not* a fifth member
  of its three-site rule; the ephemeral entry gains the third in-tool refusal and its file; a dead
  `UnicodeCategory.Surrogate` arm (unreachable for a `Rune`) was removed rather than left implying coverage.
- **m3** — the spec itself was untracked and would have shipped outside the MR. Now staged.
- **m4** — AC1's test is named `PushTool_SendsPushOnALiveTurn`, not the plan's
  `ChatAsync_ModelInvokedTool_SendsPush`. The ADR, gotchas and this file use the shipped names.
