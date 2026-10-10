using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Assembles the prompt an inbound email becomes (ADR 0095): <c>From</c> and <c>Subject</c> lines, then the body, then a
/// note per attachment that could not ride the turn. The recipient address is deliberately left out — it is the
/// credential, and the prompt is persisted, may be retained by memory (ADR 0056) and is sent to an outbound webhook
/// (ADR 0094).
///
/// <para><b>The body is <c>body-plain</c>, not <c>stripped-text</c>.</b> Stripping removes quoted and forwarded
/// content, which is precisely the payload when a user forwards a thread to their agent. <c>stripped-text</c> is only a
/// fallback. An HTML-only body is not read: there is no HTML-to-text converter in this repo on purpose (ADR 0066), so
/// the agent is told the body was skipped rather than handed markup.</para>
/// </summary>
public static class InboundEmailPrompt
{
    /// <summary>Whether the message carries any text the agent can read (a subject or a plain-text body).</summary>
    public static bool HasText(string? subject, string? bodyPlain, string? strippedText)
        => !string.IsNullOrWhiteSpace(subject) || !string.IsNullOrWhiteSpace(Body(bodyPlain, strippedText));

    /// <summary>The prompt, cut to <see cref="EmailLimits.MaxPromptChars"/>.</summary>
    public static string Build(
        string? from, string? subject, string? bodyPlain, string? strippedText, bool hasHtml,
        IReadOnlyList<string> skippedAttachments)
    {
        var prompt = new StringBuilder("You received an email.\n");
        prompt.Append("From: ").Append(OneLine(from)).Append('\n');
        prompt.Append("Subject: ").Append(OneLine(subject)).Append("\n\n");

        var body = Body(bodyPlain, strippedText);
        if (!string.IsNullOrWhiteSpace(body))
        {
            prompt.Append(body.Trim()).Append('\n');
        }
        else if (hasHtml)
        {
            prompt.Append("[The email body was HTML only and was not read.]\n");
        }

        foreach (var reason in skippedAttachments)
        {
            prompt.Append("\n[Attachment skipped: ").Append(reason).Append(']');
        }

        var (text, truncated) = WebhookLimits.Truncate(prompt.ToString().TrimEnd(),
            EmailLimits.MaxPromptChars - EmailLimits.TruncatedMarker.Length);
        return truncated ? text + EmailLimits.TruncatedMarker : text;
    }

    /// <summary>The conversation title: the subject, else the first attachment's name, else a placeholder.</summary>
    public static string Title(string? subject, string? firstFileName)
        => !string.IsNullOrWhiteSpace(subject) ? OneLine(subject)
            : !string.IsNullOrWhiteSpace(firstFileName) ? firstFileName
            : "Email";

    private static string? Body(string? bodyPlain, string? strippedText)
        => !string.IsNullOrWhiteSpace(bodyPlain) ? bodyPlain : strippedText;

    private static string OneLine(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value.ReplaceLineEndings(" ").Trim();
}
