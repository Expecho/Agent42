namespace Agent42.Agents;

/// <summary>The bounds of the outbound webhook (ADR 0094).</summary>
public static class WebhookLimits
{
    /// <summary>Cap on the reply text in one event.</summary>
    public const int MaxReplyChars = 32_000;

    /// <summary>Cap on the prompt text in one event.</summary>
    public const int MaxPromptChars = 4_000;

    /// <summary>How many delivery records the webhook grain keeps (newest win).</summary>
    public const int MaxDeliveryLog = 20;

    /// <summary>
    /// How many deliveries may wait behind the one in flight. One delivery can take ~40 s (three 10 s attempts plus
    /// the 2 s and 8 s delays), so an unbounded queue behind a dead receiver would grow with every turn; beyond this a
    /// delivery is dropped and logged as <see cref="DroppedBacklogError"/>.
    /// </summary>
    public const int MaxPendingDeliveries = 10;

    /// <summary>The longest URL the webhook accepts.</summary>
    public const int MaxUrlLength = 2_048;

    /// <summary>The log's error class for a delivery dropped because the backlog was full.</summary>
    public const string DroppedBacklogError = "dropped-backlog";

    /// <summary>The log's error class for a secret the Data Protection key ring can no longer decrypt.</summary>
    public const string SecretUnavailableError = "secret-unavailable";

    /// <summary><paramref name="text"/> cut to <paramref name="max"/> characters, and whether it was cut.</summary>
    public static (string Text, bool Truncated) Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? (text, false) : (text[..max], true);
    }
}
