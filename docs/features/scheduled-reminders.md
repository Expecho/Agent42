# Scheduled reminders

> The agent can set durable reminders that fire later (once or repeating) and prompt it to
> follow up — even when you're not watching.

## What it is

The agent can schedule a `schedule_prompt` reminder for the current conversation. When it
fires, its stored prompt is delivered to the agent as a user message and it replies — so the
conversation moves forward on its own. Reminders survive restarts (they're durable), can
repeat, and can be cancelled.

This is the **most Orleans-centric feature** in the app: it's built directly on Orleans
Reminders backed by Cosmos.

## How it works

Two stores are kept in sync deliberately: the **Orleans reminder** (the durable tick) and a
`ReminderInfo` in the grain's Cosmos-persisted state (the prompt text and metadata the tick
needs).

```mermaid
sequenceDiagram
    participant M as Model
    participant G as AgentGrain (IRemindable)
    participant OR as Orleans Reminders (Cosmos)

    M->>G: schedule_prompt(name, prompt, dueInMinutes, repeatEveryMinutes)
    G->>OR: RegisterOrUpdateReminder(name, due, period)
    G->>G: ScheduledPrompts[name] = ReminderInfo; WriteState
    Note over OR: time passes...
    OR->>G: ReceiveReminder(name, status) — reactivates grain
    G->>G: RunAsync([User: prompt], session) → reply
    G->>G: persist session; TimesFired++
    G->>G: SendAwayReplyPushAsync (ADR 0028)
```

### Scheduling — register the reminder, mirror metadata into state

```csharp
await this.RegisterOrUpdateReminder(
    name,
    TimeSpan.FromMinutes(dueInMinutes),
    TimeSpan.FromMinutes(repeatEveryMinutes));

_state.State.ScheduledPrompts[name] = new ReminderInfo(
    name, prompt, dueInMinutes, repeatEveryMinutes, DateTimeOffset.UtcNow, TimesFired: 0);
await _state.WriteStateAsync();
return $"Reminder '{name}' scheduled: first firing in {dueInMinutes} minute(s), repeating every {repeatEveryMinutes} minute(s).";
```

### Firing — the Orleans runtime callback re-enters the agent

```csharp
public async Task ReceiveReminder(string reminderName, TickStatus status)
{
    // This run's own state (ADR 0106): never ephemeral, never interactive, its own push budget and ingest flag.
    var turn = TurnContext.ForBackground();
    if (!_state.State.ScheduledPrompts.TryGetValue(reminderName, out var info)) { ...; return; }
    // no-model re-resolve (ADR 0019) ... skip if still none
    await RehydrateAttachmentUrlsAsync(_chatClientFactory.GetCapabilities(_modelId).SupportsVision);
    AlignTurnAttachmentsToHistory();
    var prompt = new ChatMessage(ChatRole.User, info.Prompt);
    var research = BeginResearchRun(prompt);                // research loop's second run site (ADR 0082)
    try { await TurnContext.RunInAsync(turn, async () => await _agent.RunAsync([prompt], _session)); }
    finally { ReleaseResearchRun(research, foldFirst: true); }
    _state.State.TurnAttachments.Add([]);
    var pruned = ApplyRetention();                          // continuous-agent 24 h window (ADR 0068)
    var serializedSession = await _agent.SerializeSessionAsync(_session);
    _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
    _state.State.ScheduledPrompts[reminderName] = info with { TimesFired = info.TimesFired + 1 };
    await _state.WriteStateAsync();
    try { await SendAwayReplyPushAsync(); } catch (Exception ex) { ... }   // ADR 0028
}
```

### Cancelling — you must unregister the durable reminder, not just drop state

```csharp
var reminder = await this.GetReminder(name);
if (reminder is not null)
    await this.UnregisterReminder(reminder);
_state.State.ScheduledPrompts.Remove(name);
await _state.WriteStateAsync();
```

`ReminderInfo` is Orleans-serializable metadata persisted with the session:

```csharp
[GenerateSerializer] [Alias("Agent42.Agents.ReminderInfo")]
public sealed record ReminderInfo(
    [property: Id(0)] string Name, [property: Id(1)] string Prompt,
    [property: Id(2)] double DueInMinutes, [property: Id(3)] double RepeatEveryMinutes,
    [property: Id(4)] DateTimeOffset CreatedAt, [property: Id(5)] int TimesFired);
```

The Cosmos reminder service is wired on the silo (resource creation disabled — the container
is pre-created in Bicep because prod Cosmos disables local auth):

```csharp
silo.UseCosmosReminderService(options =>
{
    options.ConfigureCosmosClient(sp => ValueTask.FromResult(sp.GetRequiredService<CosmosClient>()));
    options.DatabaseName = "agent42";
    options.ContainerName = "reminders";
    options.IsResourceCreationEnabled = false;
});
```

## How the Agent Framework fits

- `schedule_prompt` and the two cancel tools are `AIFunction`s on the `ChatClientAgent`, gated
  by the `scheduling` catalog key. Their `[Description]`-attributed parameters become the JSON
  schema the model fills.
- When a reminder fires, the stored prompt re-enters the **same** framework agent via
  `_agent.RunAsync([new ChatMessage(ChatRole.User, info.Prompt)], _session)` — the reminder is
  turned back into an ordinary agent turn, reusing history and any other tools.

## How Orleans fits

- `AgentGrain` implements **`IRemindable`**; durability comes from **Orleans Reminders backed
  by Cosmos** (`silo.UseCosmosReminderService`), so a reminder survives grain deactivation and
  silo restarts and reactivates the grain to deliver `ReceiveReminder`.
- `RegisterOrUpdateReminder` / `GetReminder` / `GetReminders` / `UnregisterReminder` are the
  grain's Orleans reminder APIs.
- Orleans reminders have a **1-minute minimum granularity and always recur** (no true
  one-shot) — enforced in `SchedulePromptAsync` and documented in `scheduledprompt/SKILL.md`.
- On conversation delete, `PurgeAsync` unregisters all reminders — clearing state alone would
  leave them ticking.

## Key files

| Concern | File |
|---------|------|
| Schedule/cancel tools | `src/Agent42.Agents/Scheduling/AgentGrain.Scheduling.cs` |
| `ReceiveReminder` callback | `src/Agent42.Agents/Conversations/AgentGrain.cs` — kept beside `ChatAsync` on purpose: the two turn-scoped-field clears must be read together |
| Reminder metadata | `src/Agent42.Agents/Scheduling/ReminderInfo.cs` |
| Cosmos reminder service | `src/Agent42.Backend/Program.cs`, `src/Agent42.AppHost/AppHost.cs` |
| Skill guidance | `src/Agent42.Agents/Skills/scheduledprompt/SKILL.md` |
