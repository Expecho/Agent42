namespace Agent42.Agents;

/// <summary>
/// A single message in a conversation, as exposed to clients.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatTurn")]
public sealed record ChatTurn(
    [property: Id(0)] string Role,
    [property: Id(1)] string Text);
