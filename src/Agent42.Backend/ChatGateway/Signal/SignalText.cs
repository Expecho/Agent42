using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// Converts the model's Markdown to Signal's formatting (ADR 0111, phase 3): plain text plus a <c>textStyle</c> list.
/// Signal has no markup language; each style is <c>"start:length:STYLE"</c> in <b>UTF-16 units</b> of the final text
/// (what <c>string.Length</c> counts), with BOLD, ITALIC, STRIKETHROUGH or MONOSPACE.
///
/// <para>Supported: <c>**bold**</c>, <c>*italic*</c> / <c>_italic_</c>, <c>~~strike~~</c>, inline <c>code</c>, fenced
/// blocks (MONOSPACE, the fence lines removed), <c>[text](url)</c> (as <c>text (url)</c>, since Signal has no links)
/// and <c>#</c> headings (a BOLD line). Only <b>paired</b> markers convert, so unbalanced input keeps its markers as
/// literal text. Never throws. <c>\r\n</c> is normalized first.</para>
/// </summary>
public static partial class SignalText
{
    /// <summary>Literal text, no styles.</summary>
    public static (string Text, IReadOnlyList<string> Styles) Plain(string text) => (text, []);

    public static (string Text, IReadOnlyList<string> Styles) FromMarkdown(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var builder = new Builder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Text.Append('\n');
            }

            var close = IsFence(lines[i]) ? FindClosingFence(lines, i) : -1;
            if (close > 0)
            {
                AppendFence(builder, lines, i, close);
                i = close;
            }
            else
            {
                AppendLine(builder, lines[i]);
            }
        }

        var styles = builder.Spans
            .Where(s => s.Length > 0)
            .OrderBy(s => s.Start)
            .ThenByDescending(s => s.Length)
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Start}:{s.Length}:{s.Style}"))
            .ToList();
        return (builder.Text.ToString(), styles);
    }

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private static int FindClosingFence(string[] lines, int open)
    {
        for (var j = open + 1; j < lines.Length; j++)
        {
            if (lines[j].Trim() == "```")
            {
                return j;
            }
        }

        return -1;
    }

    // The fence lines vanish; the code between them is one MONOSPACE run (its newlines are code too).
    private static void AppendFence(Builder builder, string[] lines, int open, int close)
    {
        var start = builder.Text.Length;
        builder.Text.AppendJoin('\n', lines[(open + 1)..close]);
        builder.Spans.Add(new Span(start, builder.Text.Length - start, "MONOSPACE"));
    }

    private static void AppendLine(Builder builder, string line)
    {
        var heading = HeadingPattern().Match(line);
        if (heading.Success)
        {
            var start = builder.Text.Length;
            Inline(heading.Groups["text"].Value, builder);
            builder.Spans.Add(new Span(start, builder.Text.Length - start, "BOLD"));
            return;
        }

        Inline(line, builder);
    }

    private static void Inline(string s, Builder b)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (!TryToken(s, ref i, b))
            {
                b.Text.Append(s[i]);
                i++;
            }
        }
    }

    private static bool TryToken(string s, ref int i, Builder b) => s[i] switch
    {
        '`' => TryCode(s, ref i, b),
        '*' when i + 1 < s.Length && s[i + 1] == '*' => TryPaired(s, ref i, b, "**", "BOLD"),
        '*' => TryPaired(s, ref i, b, "*", "ITALIC"),
        '_' => TryPaired(s, ref i, b, "_", "ITALIC"),
        '~' when i + 1 < s.Length && s[i + 1] == '~' => TryPaired(s, ref i, b, "~~", "STRIKETHROUGH"),
        '[' => TryLink(s, ref i, b),
        _ => false,
    };

    private static bool TryCode(string s, ref int i, Builder b)
    {
        var close = s.IndexOf('`', i + 1);
        if (close <= i + 1)
        {
            return false;
        }

        var start = b.Text.Length;
        b.Text.Append(s, i + 1, close - i - 1);
        b.Spans.Add(new Span(start, close - i - 1, "MONOSPACE"));
        i = close + 1;
        return true;
    }

    private static bool TryPaired(string s, ref int i, Builder b, string marker, string style)
    {
        var contentStart = i + marker.Length;
        var close = FindClose(s, contentStart, marker);
        if (close < 0 || !IsEmphasisContent(s, contentStart, close) || !BoundaryOk(s, i, close, marker))
        {
            return false;
        }

        var start = b.Text.Length;
        Inline(s[contentStart..close], b);
        b.Spans.Add(new Span(start, b.Text.Length - start, style));
        i = close + marker.Length;
        return true;
    }

    // A single * must not be half of a **, so it skips doubled runs; the other markers take the next occurrence.
    private static int FindClose(string s, int from, string marker)
    {
        for (var j = s.IndexOf(marker, from, StringComparison.Ordinal); j >= 0; j = s.IndexOf(marker, j + 1, StringComparison.Ordinal))
        {
            if (marker != "*" || ((j + 1 >= s.Length || s[j + 1] != '*') && s[j - 1] != '*'))
            {
                return j;
            }
        }

        return -1;
    }

    // "** bold **" and "2 * 3 * 4" are not emphasis: content must be non-empty and not padded by whitespace.
    private static bool IsEmphasisContent(string s, int start, int close)
        => close > start && !char.IsWhiteSpace(s[start]) && !char.IsWhiteSpace(s[close - 1]);

    // snake_case_names keep their underscores: an underscore pair must sit on word boundaries.
    private static bool BoundaryOk(string s, int open, int close, string marker)
    {
        if (marker != "_")
        {
            return true;
        }

        var before = open > 0 && char.IsLetterOrDigit(s[open - 1]);
        var after = close + 1 < s.Length && char.IsLetterOrDigit(s[close + 1]);
        return !before && !after;
    }

    private static bool TryLink(string s, ref int i, Builder b)
    {
        var match = LinkPattern().Match(s, i);
        if (!match.Success || match.Index != i)
        {
            return false;
        }

        Inline(match.Groups["text"].Value, b);
        b.Text.Append(" (").Append(match.Groups["url"].Value).Append(')');
        i += match.Length;
        return true;
    }

    private readonly record struct Span(int Start, int Length, string Style);

    private sealed class Builder
    {
        public StringBuilder Text { get; } = new();

        public List<Span> Spans { get; } = [];
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<text>.*?)\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\[(?<text>[^\[\]\n]+)\]\((?<url>[^()\s]+)\)")]
    private static partial Regex LinkPattern();
}
