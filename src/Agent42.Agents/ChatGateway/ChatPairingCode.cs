using System.Security.Cryptography;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// Mints and checks the one-time code that binds a chat bot to its owner (ADR 0111). The owner reads the code in
/// Agent42 and sends it to their own bot as a DM; the first sender whose DM matches becomes the owner.
///
/// <para>The alphabet drops <c>0 O 1 I L</c> so a code read off a phone screen is retyped correctly, and the match is
/// case-insensitive and ignores spaces and hyphens. 10 characters of a 31-symbol alphabet are ~49 bits: a stranger
/// cannot guess one inside <see cref="ChatGatewayLimits.PairingCodeLifetime"/> with
/// <see cref="ChatGatewayLimits.MaxPairingFailures"/> tries.</para>
/// </summary>
public static class ChatPairingCode
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 10;

    /// <summary>A fresh random code, uppercase, from <see cref="Alphabet"/>.</summary>
    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> (a DM's text) is <paramref name="code"/>. Normalised before comparing, and
    /// compared with <see cref="CryptographicOperations.FixedTimeEquals"/>: unlike a hash-keyed token lookup, this IS a
    /// secret-string comparison, so a timing difference would leak the code character by character.
    /// </summary>
    public static bool Matches(string? candidate, string? code)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(Normalize(candidate));
        var right = Encoding.UTF8.GetBytes(Normalize(code));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (c is not (' ' or '-'))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }
}
