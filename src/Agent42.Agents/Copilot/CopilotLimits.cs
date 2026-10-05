using System.Text.RegularExpressions;

namespace Agent42.Agents;

/// <summary>
/// Limits of the copilot agent kind, each measured against the Copilot runtime (CLI 1.0.90, SDK 1.0.16).
/// </summary>
public static partial class CopilotLimits
{
    /// <summary>
    /// The runtime accepts a custom tool name only if it matches this. Measured: <c>get_word</c>, <c>get-word</c>
    /// and a 65-character name are accepted. <c>get.word</c> and <c>srv:get_word</c> fail <c>session.create</c>
    /// (<c>tool name "get.word" contains invalid characters</c>), and that one bad name fails EVERY turn of the chat.
    /// A 129-character name is rejected by the provider (<c>maximum length 128</c>).
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    /// <summary>True when the runtime will accept <paramref name="name"/> as a custom tool name.</summary>
    public static bool IsValidToolName(string name) => ToolNamePattern().IsMatch(name);

    /// <summary>
    /// The runtime's own tool that lists and loads skills from <c>SkillDirectories</c>. Under
    /// <c>CopilotClientMode.Empty</c> it exists only when it is named in <c>AvailableTools</c> (measured: without it the
    /// model loads nothing). It returns a fixed 85-character acknowledgement and injects the body as context, so it
    /// adds nothing large to the mirrored history.
    /// </summary>
    public const string SkillToolName = "skill";

    /// <summary>
    /// The longest tool result the history mirror stores, in characters. Copilot's own transcript lives in SessionFs and
    /// keeps the full result. The mirror is part of the 2 MB grain session (ADR 0004), so it is bounded here as well
    /// as at each tool. That covers a tool the runtime adds, not only one Agent42 wrote.
    /// </summary>
    public const int MaxMirroredToolResultLength = 16 * 1024;

    /// <summary>Appended to a mirrored tool result cut off at <see cref="MaxMirroredToolResultLength"/>.</summary>
    public const string TruncationMarker = "\n\n[truncated]";
}
