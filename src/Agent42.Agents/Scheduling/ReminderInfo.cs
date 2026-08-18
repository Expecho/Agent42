namespace Agent42.Agents.Scheduling;

/// <summary>
/// Metadata about a reminder scheduled for a conversation. It is persisted alongside
/// the agent session; when the underlying Orleans reminder ticks, <see cref="Prompt"/>
/// is sent to the agent as a user message.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ReminderInfo")]
public sealed record ReminderInfo(
    [property: Id(0)] string Name,
    [property: Id(1)] string Prompt,
    [property: Id(2)] double DueInMinutes,
    [property: Id(3)] double RepeatEveryMinutes,
    [property: Id(4)] DateTimeOffset CreatedAt,
    [property: Id(5)] int TimesFired);
