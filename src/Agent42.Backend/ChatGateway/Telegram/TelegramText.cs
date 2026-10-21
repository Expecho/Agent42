using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// Converts the model's Markdown to Telegram's HTML parse mode (ADR 0111). HTML rather than MarkdownV2 because HTML
/// needs only <c>&amp; &lt; &gt;</c> escaped, where MarkdownV2 needs eighteen characters escaped outside entities, and
/// a single miss there fails the whole message (Hermes needed a plain-text fallback for exactly that).
///
/// <para>Supported: fenced code (with a language), inline code, bold, italic, strikethrough, links, headings (as bold),
/// block quotes and bullets. Only <b>paired</b> markers convert, so an unbalanced <c>**</c> stays literal text rather
/// than producing markup Telegram rejects. Never throws. The streamer still resends plain if Telegram rejects the
/// result anyway.</para>
/// </summary>
public static partial class TelegramText
{
    public static string ToHtml(string markdown)
    {
        // Model output can carry \r\n; a '\r' left before a closing tag ("<b>Title\r</b>") is noise in every message.
        markdown = markdown.ReplaceLineEndings("\n");
        var output = new StringBuilder(markdown.Length + 32);
        var position = 0;
        foreach (Match fence in FencePattern().Matches(markdown))
        {
            output.Append(Inline(markdown[position..fence.Index]));
            var language = fence.Groups["lang"].Value;
            var code = WebUtility.HtmlEncode(fence.Groups["code"].Value.TrimEnd('\n'));
            output.Append(language.Length > 0
                ? $"<pre><code class=\"language-{WebUtility.HtmlEncode(language)}\">{code}</code></pre>"
                : $"<pre>{code}</pre>");
            position = fence.Index + fence.Length;
        }

        output.Append(Inline(markdown[position..]));
        return output.ToString();
    }

    // Text outside fences: inline code spans are protected first, everything else is escaped and then formatted.
    private static string Inline(string text)
    {
        var output = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match code in InlineCodePattern().Matches(text))
        {
            output.Append(Format(text[position..code.Index]));
            output.Append("<code>").Append(WebUtility.HtmlEncode(code.Groups["code"].Value)).Append("</code>");
            position = code.Index + code.Length;
        }

        output.Append(Format(text[position..]));
        return output.ToString();
    }

    private static string Format(string text)
    {
        // Escape first: everything below inserts only our own tags, so model text can never inject markup.
        var html = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        html = LinkPattern().Replace(html, m => $"<a href=\"{m.Groups["url"].Value.Replace("\"", "&quot;")}\">{m.Groups["text"].Value}</a>");
        html = HeadingPattern().Replace(html, "<b>${text}</b>");
        html = BoldPattern().Replace(html, "<b>${text}</b>");
        html = ItalicPattern().Replace(html, "<i>${text}</i>");
        html = StrikePattern().Replace(html, "<s>${text}</s>");
        html = BulletPattern().Replace(html, "${indent}• ");
        return QuotePattern().Replace(html, m => $"<blockquote>{QuoteMarker().Replace(m.Value.TrimEnd('\n'), "")}</blockquote>{(m.Value.EndsWith('\n') ? "\n" : "")}");
    }

    [GeneratedRegex(@"```(?<lang>[A-Za-z0-9_+#.-]*)[^\n]*\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"`(?<code>[^`\n]+)`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"\[(?<text>[^\]\n]+)\]\((?<url>https?://[^\s)]+)\)")]
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

    // One or more consecutive "> " lines (already escaped to "&gt; ").
    [GeneratedRegex(@"(^&gt;[ \t]?.*(\n|$))+", RegexOptions.Multiline)]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"^&gt;[ \t]?", RegexOptions.Multiline)]
    private static partial Regex QuoteMarker();
}
