using System.Globalization;
using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The readable transcript of an export (ADR 0091): a header, then each turn under <c>## You</c> or the agent's name,
/// its images, its tool calls folded into <c>&lt;details&gt;</c> and its artifact cards. Pure: given
/// <see cref="ExportFiles"/> (ZIP mode) images and artifacts link to their files in the archive, otherwise they are
/// named only. Every fence is one backtick longer than the longest run inside it, so no model output can close it.
/// </summary>
public static class ConversationMarkdown
{
    public static string Render(ConversationExport export, DateTimeOffset exportedAt, ExportFiles? files = null)
    {
        var md = new StringBuilder();
        AppendHeader(md, export, exportedAt);
        foreach (var turn in export.Turns)
        {
            AppendTurn(md, turn, export.AssistantName, files);
        }

        return md.ToString();
    }

    /// <summary>A fence one backtick longer than the longest backtick run in <paramref name="content"/>, at least three.</summary>
    public static string Fence(string content)
    {
        int longest = 0, run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    private static void AppendHeader(StringBuilder md, ConversationExport export, DateTimeOffset exportedAt)
    {
        md.Append("# ").AppendLine(OneLine(export.Conversation.Title)).AppendLine();
        md.Append("- Exported: ").AppendLine(exportedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        if (export.Agent is not null || export.Conversation.AgentName is not null)
        {
            md.Append("- Agent: ").AppendLine(OneLine(export.AssistantName));
        }

        if (!string.IsNullOrEmpty(export.Model))
        {
            md.Append("- Model: ").AppendLine(OneLine(export.Model));
        }

        md.AppendLine();
        if (export.IsContinuous)
        {
            md.AppendLine("> A continuous agent chat keeps only the last 24 hours of history, so this export holds that window.")
                .AppendLine();
        }
    }

    private static void AppendTurn(StringBuilder md, ChatTurn turn, string assistantName, ExportFiles? files)
    {
        var isUser = string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase);
        md.Append("## ").AppendLine(isUser ? "You" : OneLine(assistantName)).AppendLine();
        if (!string.IsNullOrWhiteSpace(turn.Text))
        {
            md.AppendLine(turn.Text.TrimEnd()).AppendLine();
        }

        foreach (var attachment in turn.Attachments ?? [])
        {
            AppendAttachment(md, attachment, files);
        }

        foreach (var call in turn.ToolCalls ?? [])
        {
            AppendToolCall(md, call);
        }

        AppendArtifacts(md, turn.Artifacts ?? [], files);
    }

    private static void AppendAttachment(StringBuilder md, AttachmentRef attachment, ExportFiles? files)
    {
        var name = OneLine(attachment.FileName).Replace("]", "", StringComparison.Ordinal);
        if (files is not null && files.Attachments.TryGetValue(attachment.Id, out var path))
        {
            md.Append("![").Append(name).Append("](").Append(path).AppendLine(")").AppendLine();
        }
        else
        {
            md.Append("_[image: ").Append(name).AppendLine("]_").AppendLine();
        }
    }

    private static void AppendToolCall(StringBuilder md, ChatToolCall call)
    {
        md.AppendLine("<details>").Append("<summary>Tool call: ").Append(System.Net.WebUtility.HtmlEncode(OneLine(call.ToolName)))
            .AppendLine("</summary>").AppendLine();
        if (!string.IsNullOrEmpty(call.ToolInput))
        {
            md.AppendLine("Arguments:").AppendLine();
            AppendFenced(md, call.ToolInput, "json");
        }

        if (call.ToolResult is not null)
        {
            md.AppendLine("Result:").AppendLine();
            AppendFenced(md, Truncate(call.ToolResult), "");
        }

        md.AppendLine("</details>").AppendLine();
    }

    private static void AppendArtifacts(StringBuilder md, IReadOnlyList<ArtifactInfo> artifacts, ExportFiles? files)
    {
        if (artifacts.Count == 0)
        {
            return;
        }

        foreach (var artifact in artifacts)
        {
            md.Append("- Artifact: **").Append(OneLine(artifact.Title)).Append("** (").Append(artifact.Kind)
                .Append(", v").Append(artifact.Version.ToString(CultureInfo.InvariantCulture)).Append(')');
            if (files is not null && files.Artifacts.TryGetValue(ExportFiles.ArtifactKey(artifact.ArtifactId, artifact.Version), out var path))
            {
                md.Append(" — [").Append(path).Append("](").Append(path).Append(')');
            }

            md.AppendLine();
        }

        md.AppendLine();
    }

    private static void AppendFenced(StringBuilder md, string content, string info)
    {
        var fence = Fence(content);
        md.Append(fence).AppendLine(info).AppendLine(content.TrimEnd('\n', '\r')).AppendLine(fence).AppendLine();
    }

    private static string Truncate(string result)
        => result.Length <= ExportLimits.MarkdownToolResultChars
            ? result
            : string.Create(CultureInfo.InvariantCulture,
                $"{result[..ExportLimits.MarkdownToolResultChars]}\n… (truncated: {result.Length} characters in all; the JSON export has the whole result)");

    private static string OneLine(string? text)
        => (text ?? "").ReplaceLineEndings(" ").Trim();
}
