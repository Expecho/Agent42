using System.Globalization;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// Derives and vets the readable inbound-address local part an agent can opt into (ADR 0141). The slug is just another
/// local part for <see cref="EmailAddress"/>: <see cref="EmailAddress.Hash"/>, <see cref="EmailAddress.TokenFromAddress"/>
/// and <see cref="EmailAddress.Format"/> are reused unchanged. Unlike a random token it is guessable, so it is not a
/// credential.
/// </summary>
public static class PlainEmailName
{
    /// <summary>Longest slug. Well inside the 64-character local-part limit.</summary>
    public const int MaxLength = 40;

    // RFC 2142 roles plus operator addresses that may one day live on the same catch-all domain.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "postmaster", "abuse", "hostmaster", "webmaster", "admin", "administrator", "root", "security", "noreply",
        "no-reply", "mailer-daemon", "support", "info", "contact", "help", "billing", "privacy", "legal", "sales",
        "www", "mail", "smtp", "imap", "agent", "agents", "agent42", "test",
    };

    /// <summary>
    /// Lowercases, strips diacritics, turns each run of non-<c>[a-z0-9]</c> into one <c>-</c>, trims <c>-</c> and cuts to
    /// <see cref="MaxLength"/>. Returns <c>null</c> when nothing is left (an all-CJK or all-symbol name).
    /// </summary>
    public static string? Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var slug = new StringBuilder(name.Length);
        var pendingDash = false;
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (!IsSlugChar(lower))
            {
                pendingDash = true;
                continue;
            }

            if (pendingDash && slug.Length > 0)
            {
                slug.Append('-');
            }

            pendingDash = false;
            slug.Append(lower);
        }

        var result = slug.Length > MaxLength ? slug.ToString(0, MaxLength) : slug.ToString();
        result = result.TrimEnd('-');
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// True for a role/operator name, or for anything shaped like a random token (<c>agent-</c> + 32 base32 characters),
    /// which a person must not be able to choose.
    /// </summary>
    public static bool IsReserved(string slug)
        => Reserved.Contains(slug) || LooksLikeRandomToken(slug);

    private static bool IsSlugChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static bool LooksLikeRandomToken(string slug)
        => slug.Length == 38
            && slug.StartsWith("agent-", StringComparison.Ordinal)
            && slug.AsSpan(6).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz234567") < 0;
}
