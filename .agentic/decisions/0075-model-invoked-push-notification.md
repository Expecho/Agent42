---
id: 0075
date: 2026-09-16
status: accepted
tags: [push-notifications, tools, selectable-tools, prompt-injection, security, egress, incognito, no-migration]
supersedes: []
extends: [0021]
amends: [0028, 0070]
amended_by: [0106, 0111]
---

# A model-invoked push notification tool

> **Amended by [ADR 0106](0106-agentgrain-services-and-turn-context.md) (2026-10-19): the per-turn push budget is `TurnContext.PushesSent`.** The two reset sites are gone. The away-push gates read the run's own context after the run, and a Copilot run resolves its context through the in-flight registry.

> **Amended by [ADR 0111](0111-chat-gateway-for-chat-apps.md) (2026-10-21, phase 1c): `send_push_notification` also reaches
> the owner's home chat** when a chat connection has notifications on, beside Web Push and only on the sent path (a user
> with no subscribed device still gets the tool's refusal). Incognito still refuses before either.

## Context

ADR 0028 shipped the whole Web Push transport — VAPID keys, the `IUserPushGrain` subscription store, the
sender, the service-worker `push`/`notificationclick` handlers — and used it for exactly one message: a
fixed away-reply notification titled with the agent's name and bodied `"Replied while you were away."`,
fired automatically on the two asynchronous paths. Its Out-of-scope section closes with "no rich
bodies/actions/images".

Rich bodies are now wanted. The user asked for a tool the model can call to notify them about something
it decided was worth interrupting for — a watched build failing, a scheduled check finding something —
with text the model writes. **The transport needs no change at all to carry that**: `PushMessage` already
has `Title`, `Body` and `ConversationId`, and the service worker already reads all three and sets no
`tag`.

**One misreading to head off, because it is the obvious objection and it is wrong.** ADR 0028's
Out-of-scope line says "no notifying for interactive turns **the user abandons mid-stream**" — an
ADR-0046-shaped carve-out about a *dead stream*, not a general ban on notifying during a live turn. The
live-path decision below does not rest on that line at all. It rests on 0028's own service-worker
section, which already argues the opposite case: "If the user is looking at that device when their
background agent finishes, a notification titled with the agent name is acceptable and arguably wanted."

## Decision

Add `send_push_notification`, a model-invoked tool taking a `title` and a `body`, gated by a new
selectable `ToolCatalog` key `push_notification` (ADR 0021), callable from **any** path including the
live `ChatAsync` turn.

### ADR 0028's rule is NARROWED, not repealed — and the file split is the documentation

The rule "the away-reply push belongs in the two async callers, never in `ChatCoreAsync`" governs
**`SendAwayReplyPushAsync` specifically**. That method exists to reach a user who is *not* watching, so
firing it from the live stream would be self-defeating; both of its placement traps (the `_modelId` gate
in `EnqueuePromptAsync`, the post-`WriteStateAsync` position in `ReceiveReminder`) stand untouched.

A model-invoked notification is a different thing. The model asked for it, about something it judged
worth interrupting for, so it may fire from any path. That is why the tool lives in a **new partial**,
`src/Agent42.Agents/Push/AgentGrain.PushTool.cs`, a sibling of `src/Agent42.Agents/Push/AgentGrain.Push.cs`
rather than an addition to it: the split is itself the record that these are two rules, not one.

### The counter resets in TWO places, and that is the trap

`_pushesThisTurn` bounds notifications per turn. `ChatAsync` and `EnqueuePromptAsync` both **drain**
`ChatCoreAsync`, so one reset at the top of that shared core covers both — and covers any future caller,
which a per-entrypoint clear could not. `ReceiveReminder` is the exception: it calls `_agent.RunAsync`
directly and never enters `ChatCoreAsync`, so it resets the field itself in its existing reset block.

This is deliberately **not** the `_ephemeral` shape of "set it in all three entrypoints". Those fields
carry a *different value* per path, so only the caller knows what to set; this one always resets to `0`.

Miss the second site and a **repeating** reminder inherits the previous tick's count and goes permanently
silent after three notifications, for the life of the activation — the ADR 0012 `_turnLocation` bug shape
exactly. `PushTool_CounterResetsOnTheReminderPath` pins it and was **mutation-checked**: removing that one
line fails that test and nothing else.

There is **no fourth path**. `GenerateFollowUpsAsync` (ADR 0036) and `RecordImageTurnAsync` (ADR 0020)
both run a model with no `Tools` at all, and ADR 0070's `ask_agent` calls `ChatAsync(…, interactive: false)`,
which drains `ChatCoreAsync` and is covered by the first site.

### The away-reply push is suppressed when the tool already pushed

The service worker sets **no `tag`**, so notifications stack rather than coalesce. Without this, one async
reply whose model called the tool would put two entries in the shade — the model's "Build finished"
followed by the generic "Replied while you were away." Both call sites therefore gain `&& _pushesThisTurn == 0`.

**This is a behaviour change on ADR 0028's own paths**, which is why 0028 carries an inline amendment
banner rather than only a gotcha edit: one sentence of its Decision ("A user actively chatting receives
their reply in-stream; no push is generated for that turn") no longer describes the system.

### Refused in incognito chats

`.agentic/decisions/0034-ephemeral-chat.md` refuses *scheduling* partly because a reminder "persists the
session **and sends the away-reply Web Push (ADR 0028)** — **both** break the guarantee." ADR 0034
therefore already names a Web Push, by itself, as guarantee-breaking. Two concrete reasons it is right
about this tool too:

- The title and body are a **durable, plaintext, at-rest artifact** in the OS notification shade, derived
  from a conversation that promised to leave no trace. That is a *new category* of leak for ADR 0034, not
  an inherited ADR 0028 residual.
- An incognito chat has **no sidebar row**, so the notification would become the *only* handle to it, on
  any device — the opposite of "not listed".

So the tool refuses when `_ephemeral`, using the in-tool guard shape `SchedulePromptAsync` and
`read_web_document` already use (never a `BuildAgent` drop — that runs before the turn's flag is known).
Because this **upholds** ADR 0034 rather than amending it, 0034 gets no banner. An earlier draft allowed
it by analogy to "images persist — accepted"; that was the wrong precedent, and it was rejected.

### The sanitizer is a DENYLIST, which is the opposite of ADR 0066, deliberately

`WebDocumentLimits.SanitizeFileName` uses a strict ASCII **allowlist** because its sink is the **system
prompt**, where "which characters are dangerous" is the wrong question to be answering.

This sink is different: `showNotification(title, { body })` renders **plain text** — no HTML parse, no
`innerHTML`, no prompt. An ASCII allowlist here would mangle ordinary Dutch text and every emoji for no
security gain whatsoever. So `PushToolLimits.SanitizeNotificationText` instead strips what can
*misrepresent* the text — Unicode **control** (Cc) and **format** (Cf) characters, the latter being the
half that earns it its place because it holds the bidi overrides (U+202E) that really can reorder rendered
notification text into something the model never wrote — collapses whitespace runs including newlines,
hard-caps the length, and **returns empty when nothing usable remains**, which the tool treats as a
refusal. `Sanitize_KeepsAccentedTextAndEmoji` is the test that says no to anyone "fixing" this into an
allowlist for consistency.

One honest cost: stripping Cf breaks a ZWJ emoji sequence into its components.

### The tool reads `IUserPushGrain` itself

`IPushNotificationSender.SendAsync` returns `Task`, **never throws**, and silently early-returns when the
user has no subscriptions — so the sender can tell the tool nothing. Rather than widen that contract
across four host registrations, the tool does its own `GetGrain<IUserPushGrain>(_userId).GetAllAsync()`
to answer the one user-visible question: *are any devices subscribed?*

**This does not make the tool able to report delivery, and it must not pretend to.** Whether a push
arrived is unknowable by design (0028's best-effort, prune-on-404 contract). The return string and the
tool description both say "queued to N device(s)", never "delivered".

### No `SKILL.md`, deliberately

ADR 0067's headline lesson is that **the rule the model actually reads is in the tool descriptions**,
which ship in the JSON schema every turn. A skill would also mint a `skill:{name}` picker row that lands
OFF for anyone who ever pressed Save (ADR 0048) — a second silent-off key for one feature. All the
guidance this tool needs is in the `AIFunctionFactory` description and the two `[Description]` attributes.

## Consequences

### Attacker-chosen text can now reach the user's lock screen

The notification text is **model-authored**, so every inbound prompt-injection channel in the app — ADR
0011 (web search), 0024/0067 (MCP results), 0027 (followed links), 0049 (uploaded `SKILL.md`), 0066 (web
documents) — can now put text of an attacker's choosing in front of the user, in the most trusted place
the OS has. **Sanitization bounds the characters, not the meaning**: "Your bank needs you to call
555-0100" sanitizes perfectly.

Sharper, and easy to miss: **this is reachable from a key-holder endpoint.** A holder of an ADR 0022
`/api/agent-run` key could already cause the *fixed* away-reply push to fire; with this tool they can cause
one whose **text they control**, on the *owner's* devices. That is a real escalation of that endpoint, and
it is accepted: notifying is a plausible reason to drive an agent from a cron job, and that surface already
notifies by design.

**The `/api/agent-mcp` surface (ADR 0070) is the exception, and it is excluded.** That ADR chose
deliberately that an `ask_agent` turn lands in the owner's sidebar **silently**, because the caller already
has the reply in hand. Since the new key is in `AllSelectableToolKeys` and that surface binds `null == all`,
shipping without a filter would have reversed a recorded decision **by default** — so
`AgentMcpRunner.WithoutMemory` becomes `WithoutMemoryOrPush` and drops both keys at bind time. The
asymmetry with `/api/agent-run` is deliberate and argued in that method's docstring.
`AskAgent_SendsNoPushNotification` pins it (mutation-checked, with
`AskAgent_PushExclusionIsThisSurfaceOnly` as the control that keeps it honest).

### The egress paragraph ADR 0028 wrote no longer describes the payload

0028 reasoned that the payload is **RFC 8291 end-to-end encrypted**, so the push service cannot read it,
and recorded the residual narrowly: "the title is the **user-chosen agent name** (a user could have made
it sensitive)". The encryption is unchanged and still holds. What no longer holds is the description of
what is inside: title *and* body are now **model-authored, conversation-derived** text, which can carry
anything the conversation contains — document excerpts (0007), the user's street address (0012), tool and
web content. The transit risk is the same; the sensitivity of the plaintext is not.

### It lands OFF for anyone who ever pressed Save

A stored selection is an explicit list, so the new key is absent from every selection saved before this
shipped, and there is deliberately **no migration**. One press of Save in Preferences → Tools restores it
for regular chats; an already-bound **agent** chat has no in-UI remedy (ADR 0003's immutable snapshot,
and the popover is hidden there). It fails **safe** — a tool that can put model-authored text on a lock
screen stays off until deliberately enabled. `LegacyAgentSnapshot_DoesNotEnablePushNotification` pins it.

### A notification can outlive the turn that sent it

ADR 0070's `AgentMcpRunner` abandons the enumerator on timeout, which **loses the turn** — tokens spent,
conversation listed with no messages. A push already fired by the tool **survives** that, so a user can
receive a notification that deep-links to a conversation containing nothing. Recorded because it is
expensive to rediscover, not because it is being fixed here.

### Nothing else moves

No new secret, Cosmos container, blob container, grain, folder, `excludedPaths` entry, Bicep change,
package or `[Id(n)]` — and therefore no migration. **No `sw.js` `CACHE` bump is due**: the service worker
already reads `title`/`body`/`conversationId` and sets no `tag`, the picker is data-driven off
`/api/tools`, and its "Built-in" group is everything that is not a `skill:` or `mcp:` row — so a new
static key renders with no frontend change at all. `UsageSpanProjector` whitelists by *operation* name
(`chat`/`execute_tool`), never per tool, so ADR 0031's tool-calls-by-name table picks this up for free.

## Alternatives considered

- **Extend `SendAwayReplyPushAsync` instead of adding a sibling partial.** Rejected: it would fuse two
  rules with opposite placement constraints into one method, and the next reader would have no way to
  see that "never from `ChatCoreAsync`" applies to one of them and not the other.
- **Allow it in incognito**, by analogy with images (whose blob bytes persist as an accepted residual).
  Rejected — see the incognito section; ADR 0034 singles a Web Push out by name.
- **Let both notifications fire on an async turn.** Rejected: with no `tag` they stack, and the generic
  one adds nothing beside the model's own text.
- **An ASCII allowlist, for consistency with ADR 0066.** Rejected — different sink, and it would destroy
  ordinary non-English text and emoji for no gain.
- **A cross-turn rate limit or quiet hours.** Out of scope; the per-turn cap is the bound that ships.
- **Leaving the tool available on `/api/agent-mcp`** and merely recording the escalation. Rejected after
  review: ADR 0070 makes silence on that surface an explicit decision, and `null == all` would have
  reversed it for every exposed agent without anyone choosing to. Excluding it costs one clause in a filter
  that already existed for memory. The reverse argument — that a push returns no data to the caller, unlike
  memory recall, so it is a weaker escalation — is true but does not bear on the point: the harm is to the
  *owner*, whose lock screen it is, not to the caller.

## Verification

The offline leg **was** run against DevHost (the sender is `NullPushNotificationSender` and
`PushConfig.Enabled` is false there, so `POST /api/push/subscriptions` — which is **not** gated on that
flag — was used to seed a device first). Measured:

| Case | Observed |
|---|---|
| `GET /api/tools` | `push_notification \| Push notifications` present |
| One notification | `Notification queued to 1 device(s), titled "Build finished". …` |
| Four calls in one turn | calls 1–3 queued; call 4 → `I have already sent 3 notifications during this turn, which is the limit.` |
| Next turn, same conversation | queues again — reset site 1 observed in the running app |
| Incognito turn | `I cannot send a notification from an incognito chat. …` |
| Key deselected via `PUT /api/conversations/{id}/tools` | `Error: Requested function "send_push_notification" not found.` — the tool is not registered at all |

**What that leg does not prove, and cannot:** with the null sender no notification is ever produced, so
it shows the tool is registered, gated, capped and returning the right strings — never delivery. Real
delivery stays a manual Android-device check, exactly as ADR 0028 recorded for its own frontend leg.

One observation worth recording: DevHost's `CannedChatClient` does **not** consult `options.Tools` before
emitting a call (unlike `FakeChatClient`, which returns null on a no-tools model), so the deselected case
shows a tool *call* whose result is the framework's "not found". That is a fake artifact, not a product
behaviour — and it is what makes the gate visible offline.
