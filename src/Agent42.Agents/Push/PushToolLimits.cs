using System.Globalization;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// The budgets and the text policy the model-invoked <c>send_push_notification</c> tool runs inside
/// (ADR 0075), the same discipline <see cref="GeoToolLimits"/> and <see cref="WebDocumentLimits"/>
/// carry for their own tools.
///
/// <para>Two independent bounds live here and must not be conflated. The <b>text</b> caps
/// (<see cref="MaxTitleLength"/>, <see cref="MaxBodyLength"/>) bound what reaches the device's
/// notification shade — and, because a tool's return value is persisted into the single 2 MB
/// <c>agent-state</c> Cosmos item (ADR 0004), what the model gets told it sent. The <b>per-turn</b>
/// cap (<see cref="MaxPushesPerTurn"/>) bounds how many notifications one turn can raise at all: the
/// tool is model-driven and reachable from <c>/api/agent-run</c> (ADR 0022), <c>/api/agent-mcp</c>
/// (ADR 0070) and a <b>repeating</b> reminder, so no human is deciding to send one more.</para>
/// </summary>
public static class PushToolLimits
{
    /// <summary>
    /// Maximum length of a notification title, in characters. Android's notification shade truncates a
    /// title to roughly one line, so a longer one is not "more title" — it is a title with its end
    /// hidden. Kept deliberately short so the useful words have to come first.
    /// </summary>
    public const int MaxTitleLength = 100;

    /// <summary>
    /// Maximum length of a notification body, in characters. Enough for two or three sentences — an
    /// expanded notification shows a few lines and no more — and small enough that three of them in one
    /// turn cannot make a dent in the session's 2 MB budget (ADR 0004).
    /// </summary>
    public const int MaxBodyLength = 300;

    /// <summary>
    /// Maximum number of notifications one turn may actually send. A refused call does <b>not</b> consume
    /// budget — the counter is incremented on the send, not on the attempt — so a model that writes three
    /// unusable titles has not spent its allowance.
    ///
    /// <para>The bound matters more than it looks. The service worker sets <b>no <c>tag</c></b>
    /// (ADR 0028), so notifications <i>stack</i> in the shade rather than replacing one another: without
    /// a cap, one turn could bury the user's lock screen. It is a per-<b>turn</b> cap only — there is
    /// deliberately no cross-turn rate limit, which is recorded as out of scope in ADR 0075 rather than
    /// implied to be covered here.</para>
    /// </summary>
    public const int MaxPushesPerTurn = 3;

    // U+FFFD. EnumerateRunes substitutes it for any ill-formed UTF-16 subsequence — a lone surrogate,
    // typically — so testing for it is how a broken pair is dropped without hand-rolling pair arithmetic.
    private const int ReplacementChar = 0xFFFD;

    /// <summary>
    /// Cleans one piece of <b>model-authored</b> notification text — a title or a body — and caps it at
    /// <paramref name="maxLength"/>. Returns an empty string when nothing usable survives, which the
    /// caller must treat as a refusal rather than sending a blank notification.
    ///
    /// <para><b>This is deliberately a denylist, which is the opposite of
    /// <see cref="WebDocumentLimits.SanitizeFileName"/>, and the divergence is the point.</b> That one
    /// allowlists ASCII because its sink is the <i>system prompt</i>, where the right question is "what
    /// may enter" and mangling a name is a cost worth paying. This sink is
    /// <c>showNotification(title, { body })</c> — the service worker renders it as <b>plain text</b>,
    /// with no HTML parse (no <c>innerHTML</c>) and no prompt involved. An ASCII allowlist here would
    /// destroy ordinary Dutch text and every emoji for no security gain whatsoever, so the rule is
    /// instead: strip what can <i>misrepresent</i> the text, keep everything that merely reads
    /// unusually.</para>
    ///
    /// <para>What is stripped, and why each one: <b>control</b> characters (Cc) and <b>format</b>
    /// characters (Cf) — Cf is the half that earns this its place, because it holds the bidi overrides
    /// (U+202E and friends) that reorder rendered text into something the model never wrote, and the
    /// zero-width spaces and joiners that hide word boundaries; unassigned code points and the
    /// replacement character, which cannot render. Whitespace <b>runs collapse to a single space</b>,
    /// newlines included: a notification body is one paragraph, and a model that lays out fake structure
    /// ("---", a blank line, a heading) is writing a shape the shade will not honour anyway.</para>
    ///
    /// <para>One honest consequence: stripping Cf breaks a ZWJ emoji sequence into its components, so a
    /// family emoji arrives as separate people. That is accepted — it is a legibility cost on an unusual
    /// input, not a correctness one, and it is a much smaller price than admitting U+200D to keep it.</para>
    /// </summary>
    public static string SanitizeNotificationText(string? raw, int maxLength)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(raw.Length, maxLength));
        var pendingSpace = false;

        // Runes, not chars, so a surrogate pair is judged and appended as the one character it is —
        // the cap can then never cut an emoji in half, and no pair arithmetic is written by hand.
        foreach (var rune in raw.EnumerateRunes())
        {
            // Whitespace is tested FIRST because '\n', '\r' and '\t' are Control characters too, and
            // dropping them outright would weld two lines into one word. A run becomes a single space,
            // held back until something is actually kept after it — which is also what silently drops a
            // leading or trailing run without a Trim pass.
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (IsDroppable(rune))
            {
                continue;
            }

            var width = rune.Utf16SequenceLength + (pendingSpace ? 1 : 0);
            if (builder.Length + width > maxLength)
            {
                // Cut, rather than refuse: an over-long title is still a usable notification, and the
                // model is told the text was shortened. No ellipsis is appended — it would spend cap on
                // punctuation the shade already implies by truncating.
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    // Everything here is text that cannot honestly render: control and format characters (see the
    // SanitizeNotificationText remarks — Cf carries the bidi overrides), code points no Unicode version
    // has assigned, private-use code points (whose glyph is whatever a particular font decided, so they
    // say nothing portable), unpaired surrogates (which EnumerateRunes has already turned into U+FFFD),
    // and the replacement character itself.
    private static bool IsDroppable(Rune rune)
        => rune.Value == ReplacementChar
            || Rune.GetUnicodeCategory(rune)
                is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.PrivateUse;
}
