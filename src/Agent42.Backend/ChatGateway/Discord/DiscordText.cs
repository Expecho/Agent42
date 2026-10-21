using System.Text;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// Adapts the model's Markdown to Discord (ADR 0111). Discord renders Markdown natively (bold, italic, strikethrough,
/// code, fences with a language, quotes, lists, headings, masked links), so the text passes through, except for
/// <b>tables</b>, which Discord shows as raw pipes: each row becomes a bullet of <c>header: value</c> pairs. Fenced code
/// is left exactly as written, tables in it included. Mentions need no escaping here: every send carries
/// <c>allowed_mentions</c> none, so <c>@everyone</c> in a reply is inert text. Never throws.
/// </summary>
public static partial class DiscordText
{
    public static string ToDiscord(string markdown)
    {
        markdown = markdown.ReplaceLineEndings("\n");
        var output = new StringBuilder(markdown.Length);
        var position = 0;
        foreach (Match fence in FencePattern().Matches(markdown))
        {
            output.Append(Tables(markdown[position..fence.Index]));
            output.Append(fence.Value);
            position = fence.Index + fence.Length;
        }

        output.Append(Tables(markdown[position..]));
        return output.ToString();
    }

    private static string Tables(string text)
        => TablePattern().Replace(text, table =>
        {
            var rows = table.Value.TrimEnd('\n').Split('\n').Select(Cells).ToList();
            var headers = rows[0];
            var bullets = rows.Skip(2).Select(cells => "• " + string.Join(", ", cells.Select((cell, i) =>
                i < headers.Count && headers[i].Length > 0 ? $"{headers[i]}: {cell}" : cell)));
            return string.Join('\n', bullets) + (table.Value.EndsWith('\n') ? "\n" : "");
        });

    private static List<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    // A header row, a |---|:---:| separator row, then the body rows, each line starting with '|'.
    [GeneratedRegex(@"^\|.*\|[ \t]*\n\|[ \t]*:?-{2,}.*\n(\|.*(\n|$))*", RegexOptions.Multiline)]
    private static partial Regex TablePattern();
}
