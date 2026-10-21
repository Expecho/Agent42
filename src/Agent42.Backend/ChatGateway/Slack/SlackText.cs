using System.Text;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// Converts the model's Markdown to Slack mrkdwn (ADR 0111), sent as <c>text</c>. Chosen over Slack's
/// <c>markdown_text</c> argument because Slack documents mrkdwn's escaping and not <c>markdown_text</c>'s: escaping
/// <c>&amp; &lt; &gt;</c> is what keeps a reply from ever forming a <c>&lt;!channel&gt;</c>, <c>&lt;!here&gt;</c> or
/// <c>&lt;@U…&gt;</c> control sequence, so model output can never ping a channel. Literal <c>@channel</c> text is not
/// a mention either: the gateway never sets <c>link_names</c>.
///
/// <para>Supported: fenced code (Slack has no language tag, so it is dropped), inline code, bold, italic,
/// strikethrough, links (<c>&lt;url|text&gt;</c>), headings (as bold), block quotes and bullets. Only paired markers
/// convert. Never throws.</para>
/// </summary>
public static partial class SlackText
{
    public static string ToMrkdwn(string markdown)
    {
        markdown = markdown.ReplaceLineEndings("\n");
        var output = new StringBuilder(markdown.Length + 16);
        var position = 0;
        foreach (Match fence in FencePattern().Matches(markdown))
        {
            output.Append(Inline(markdown[position..fence.Index]));
            output.Append("```\n").Append(Escape(fence.Groups["code"].Value.TrimEnd('\n'))).Append("\n```");
            position = fence.Index + fence.Length;
        }

        output.Append(Inline(markdown[position..]));
        return output.ToString();
    }

    /// <summary>Literal text, safe to send: only Slack's three control characters are escaped.</summary>
    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Inline(string text)
    {
        var output = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match code in InlineCodePattern().Matches(text))
        {
            output.Append(Format(text[position..code.Index]));
            output.Append('`').Append(Escape(code.Groups["code"].Value)).Append('`');
            position = code.Index + code.Length;
        }

        output.Append(Format(text[position..]));
        return output.ToString();
    }

    private static string Format(string text)
    {
        // Escape first: everything below inserts only our own sequences, so model text can never form one.
        var mrkdwn = Escape(text);
        mrkdwn = LinkPattern().Replace(mrkdwn, m => $"<{m.Groups["url"].Value}|{m.Groups["text"].Value.Replace("|", "¦")}>");
        // Single markers first: Slack's bold IS a single '*', so bold (and headings, which become bold) converted
        // before italic would be turned into italic.
        mrkdwn = ItalicPattern().Replace(mrkdwn, "_${text}_");
        mrkdwn = BoldPattern().Replace(mrkdwn, "*${text}*");
        mrkdwn = HeadingPattern().Replace(mrkdwn, "*${text}*");
        mrkdwn = StrikePattern().Replace(mrkdwn, "~${text}~");
        mrkdwn = BulletPattern().Replace(mrkdwn, "${indent}• ");
        // A quote is a literal '>' at the start of a line; it can't open a control sequence there.
        return QuotePattern().Replace(mrkdwn, ">");
    }

    [GeneratedRegex(@"```[^\n]*\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"`(?<code>[^`\n]+)`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"\[(?<text>[^\]\n]+)\]\((?<url>https?://[^\s)|]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^#{1,6}[ \t]+(?<text>.+?)[ \t]*#*[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(?<text>.+?)(?<=\S)\1")]
    private static partial Regex BoldPattern();

    // A single * or _ pair, not inside a word (snake_case_names stay literal).
    [GeneratedRegex(@"(?<![\w*])([*_])(?=\S)(?<text>[^*_\n]+?)(?<=\S)\1(?![\w*])")]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"~~(?=\S)(?<text>.+?)(?<=\S)~~")]
    private static partial Regex StrikePattern();

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-*+][ \t]+", RegexOptions.Multiline)]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^&gt;", RegexOptions.Multiline)]
    private static partial Regex QuotePattern();
}
