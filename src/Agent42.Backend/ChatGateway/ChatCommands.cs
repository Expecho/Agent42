using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>A chat command the owner typed: its canonical name and the rest of the line.</summary>
public sealed record ChatCommand(string Name, string Arguments);

/// <summary>
/// Recognizes chat commands (ADR 0111). A command is the <b>first token</b> of a message: the platform's prefix and a
/// name (<c>/new</c>, Slack <c>!new</c>), optionally addressed to this bot (<c>/new@agent42_bot</c>, Telegram's group
/// form). Anything else is ordinary text, so a message starting <c>/etc/hosts</c> or addressed to another bot is never
/// a command. A command-shaped token that names no command answers with help, which also covers Telegram's
/// <c>/start</c>.
///
/// <para>Dispatch is a name → handler table in <see cref="ChatTurnPipeline"/>, never an if-chain, and commands run
/// inline, ahead of any queued turn, so <c>/stop</c> never waits behind the turn it stops.</para>
/// </summary>
public static partial class ChatCommands
{
    public const string New = "new";
    public const string Stop = "stop";
    public const string Model = "model";
    public const string Agent = "agent";
    public const string SetHome = "sethome";
    public const string Status = "status";
    public const string Help = "help";

    /// <summary>Every command, in the order help and Telegram's menu list them.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        (New, "Start a new conversation"),
        (Stop, "Stop the reply in progress"),
        (Model, "Show or switch the model: /model list, /model <id>, /model default"),
        (Agent, "Talk to a stored agent: /agent list, /agent <name>, /agent none"),
        (SetHome, "Send notifications and scheduled results to this chat"),
        (Status, "Show the model, agent and conversation in use"),
        (Help, "List the commands"),
    ];

    /// <summary>Commands that work in a group without mentioning the bot (Hermes: otherwise /stop is unreachable).</summary>
    public static readonly IReadOnlySet<string> AllowedWithoutMention = new HashSet<string>(StringComparer.Ordinal) { New, Stop, Help };

    /// <summary>The command <paramref name="text"/> starts with, or <c>null</c> when it is ordinary text.</summary>
    public static ChatCommand? Parse(string? text, char prefix, string? botUserName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != prefix)
        {
            return null;
        }

        var end = trimmed.IndexOfAny([' ', '\n', '\t']);
        var token = end < 0 ? trimmed[1..] : trimmed[1..end];
        var match = TokenPattern().Match(token);
        if (!match.Success)
        {
            return null;
        }

        var addressee = match.Groups["bot"];
        if (addressee.Success
            && !string.Equals(addressee.Value, botUserName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = match.Groups["name"].Value.ToLowerInvariant();
        var arguments = end < 0 ? "" : trimmed[end..].Trim();
        return All.Any(c => c.Name == name) ? new ChatCommand(name, arguments) : new ChatCommand(Help, "");
    }

    /// <summary>The help text, listing every command with this platform's prefix.</summary>
    public static string HelpText(char prefix)
        => "Commands:\n" + string.Join('\n', All.Select(c => $"{prefix}{c.Name} — {c.Description}"));

    [GeneratedRegex("^(?<name>[A-Za-z][A-Za-z0-9_]{0,31})(@(?<bot>[A-Za-z0-9_]{1,64}))?$")]
    private static partial Regex TokenPattern();
}
