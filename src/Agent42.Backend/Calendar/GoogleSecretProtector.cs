using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Agent42.Backend;

/// <summary>
/// Encrypts and decrypts a user's stored Google OAuth tokens (ADR 0053). The API layer protects them
/// before they reach <c>IUserGoogleGrain</c> (so the grain only ever stores ciphertext), and
/// <c>GoogleCalendarService</c> unprotects them only when building a call.
///
/// <para>Distinct from <see cref="IMcpSecretProtector"/> and <c>IProviderSecretProtector</c> on
/// purpose: a Google refresh token is its own concern with its own Data-Protection scope, so its key
/// rotation is independent of the other two. (Each protector's purpose is a private const rather than a
/// parameter, so scopes cannot be shared by reuse — hence a third protector.)</para>
/// </summary>
public interface IGoogleSecretProtector
{
    /// <summary>Encrypts a plaintext token for persistence.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored ciphertext, or returns <c>null</c> when it is blank or undecryptable (a rotated
    /// key ring). <b>Never throws.</b> A null here degrades the connection to "not connected" — the
    /// curated notice the user can act on — rather than throwing into the middle of a turn.
    /// </summary>
    string? Unprotect(string? ciphertext);
}

/// <summary>
/// <see cref="IGoogleSecretProtector"/> over ASP.NET Core Data Protection. The key ring is persisted
/// out-of-process in prod (the <c>dataprotection</c> blob container, ADR 0018) so a restart does not
/// rotate it; DevHost/tests use the default ephemeral ring — see <c>Program.cs</c>.
///
/// <para><b>The failure this class makes survivable is specific and silent.</b> With an unpersisted
/// ring in production, a restart makes every stored refresh token undecryptable. <c>Unprotect</c>
/// returns null rather than throwing, so the user's calendar simply stops answering — the tools report
/// "not connected" and the fix is to reconnect. That is the least-bad behaviour, but it is why the
/// warning below is worth reading in the logs.</para>
/// </summary>
public sealed class GoogleSecretProtector : IGoogleSecretProtector
{
    // A versioned, isolated purpose string: rotating it (…v2) invalidates old ciphertext deliberately,
    // and it keeps Google tokens in their own protection scope, unreadable by any other protector.
    private const string Purpose = "Agent42.GoogleCalendar.Secret.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<GoogleSecretProtector> _logger;

    public GoogleSecretProtector(IDataProtectionProvider provider, ILogger<GoogleSecretProtector> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex,
                "A stored Google credential could not be decrypted; treating the user as not connected. "
                + "If this persists after a restart, the Data Protection key ring is not being persisted "
                + "and every connection will need to be re-made.");
            return null;
        }
    }
}
