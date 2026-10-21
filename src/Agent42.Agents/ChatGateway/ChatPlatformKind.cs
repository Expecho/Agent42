namespace Agent42.Agents;

/// <summary>
/// The chat platforms a <see cref="ChatConnection"/> can target (ADR 0111). Persisted as these exact strings,
/// never as an enum ordinal, so a reordering or an added platform can never re-point a stored connection at a
/// different platform (the rule ADR 0110 applies to <c>ProviderKind</c>).
/// </summary>
public static class ChatPlatformKind
{
    public const string Telegram = "Telegram";
    public const string Slack = "Slack";
    public const string Discord = "Discord";
    public const string WhatsApp = "WhatsApp";
    public const string Signal = "Signal";

    /// <summary>Every platform kind, in the order the Preferences picker lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Telegram, Slack, Discord, WhatsApp, Signal];

    /// <summary>Whether <paramref name="kind"/> is exactly one of <see cref="All"/> (ordinal, case-sensitive).</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
