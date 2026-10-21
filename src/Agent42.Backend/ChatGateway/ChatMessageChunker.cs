namespace Agent42.Backend;

/// <summary>
/// Splits a reply that is longer than a platform allows into ordered messages (ADR 0111), the shape of Hermes'
/// <c>truncate_message</c>: split at a newline, else a space, never inside an inline code span, and when a split
/// falls inside a fenced code block, close the fence at the end of one message and reopen it (with its language) at
/// the start of the next, so every message renders on its own. Multi-message replies get <c>(1/3)</c> markers, and a
/// runaway reply is cut at <c>maxChunks</c>.
///
/// <para>Lengths are <c>string.Length</c>, i.e. UTF-16 code units. Telegram counts entity offsets in UTF-16 units,
/// and for every other platform UTF-16 units are at least the character count, so it is never too long.</para>
/// </summary>
public static class ChatMessageChunker
{
    // Room for "\n\n(99/99)" on every chunk, so adding markers never pushes one over the limit.
    private const int MarkerReserve = 12;
    private const string FenceClose = "\n```";
    private const string TruncatedNote = "\n…(truncated)";

    public static IReadOnlyList<string> Split(string text, int maxLength, int maxChunks = int.MaxValue)
    {
        if (text.Length <= maxLength)
        {
            return [text];
        }

        var chunks = Cut(text, maxLength);
        if (chunks.Count > maxChunks)
        {
            chunks = chunks.Take(maxChunks).ToList();
            var last = chunks[^1];
            var room = maxLength - MarkerReserve - TruncatedNote.Length;
            chunks[^1] = (last.Length > room ? last[..SafeCut(last, room)] : last) + TruncatedNote;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i] += $"\n\n({i + 1}/{chunks.Count})";
        }

        return chunks;
    }

    private static List<string> Cut(string text, int maxLength)
    {
        var chunks = new List<string>();
        var rest = text;
        string? openFence = null;

        while (rest.Length > 0)
        {
            var prefix = openFence is null ? "" : openFence + "\n";
            if (prefix.Length + rest.Length <= maxLength - MarkerReserve)
            {
                chunks.Add(prefix + rest);
                break;
            }

            var budget = Math.Max(1, maxLength - MarkerReserve - prefix.Length - FenceClose.Length);
            var (cut, skip) = FindCut(rest, budget);
            var piece = rest[..cut];
            rest = rest[(cut + skip)..];

            var fenceAfter = FenceAfter(openFence, piece);
            chunks.Add(prefix + piece.TrimEnd() + (fenceAfter is null ? "" : FenceClose));
            openFence = fenceAfter;
        }

        return chunks;
    }

    // Where to end this chunk (exclusive) and how many separator characters to drop after it.
    private static (int Cut, int Skip) FindCut(string rest, int budget)
    {
        var window = rest[..Math.Min(budget, rest.Length)];
        var floor = budget / 2;

        var newline = window.LastIndexOf('\n');
        if (newline >= floor)
        {
            return (newline, 1);
        }

        var space = window.LastIndexOf(' ');
        var (cut, skip) = space >= floor ? (space, 1) : (SafeCut(rest, window.Length), 0);

        // Never end inside an inline code span: an odd number of single backticks on the last line means one is open,
        // so back up to just before it.
        var lastLine = rest[..cut];
        lastLine = lastLine[(lastLine.LastIndexOf('\n') + 1)..];
        if (!lastLine.TrimStart().StartsWith("```", StringComparison.Ordinal) && lastLine.Count(c => c == '`') % 2 == 1)
        {
            var tick = rest.LastIndexOf('`', cut - 1);
            if (tick > budget / 4)
            {
                return (tick, 0);
            }
        }

        return (cut, skip);
    }

    // Never split a surrogate pair.
    private static int SafeCut(string text, int cut)
        => cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1]) ? cut - 1 : cut;

    // The fence opener line ("```python") still open after this piece, or null when the piece leaves no fence open.
    private static string? FenceAfter(string? openFence, string piece)
    {
        foreach (var line in piece.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                openFence = openFence is null ? trimmed : null;
            }
        }

        return openFence;
    }
}
