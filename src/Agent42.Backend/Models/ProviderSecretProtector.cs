using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Agent42.Backend;

/// <summary>
/// Encrypts and decrypts user-configured provider API keys (ADR 0018). The API layer protects a key
/// before it reaches the overlay grain (so the grain only ever stores ciphertext), and the resolver
/// unprotects it only when building a client.
/// </summary>
public interface IProviderSecretProtector
{
    /// <summary>Encrypts a plaintext secret for persistence.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored ciphertext, or returns <c>null</c> when it is blank or undecryptable (a
    /// rotated key ring). Never throws: an overlay is best-effort, so an unreadable key degrades the
    /// provider to "no key" rather than bricking resolution (ADR 0018).
    /// </summary>
    string? Unprotect(string? ciphertext);
}

/// <summary>
/// <see cref="IProviderSecretProtector"/> over ASP.NET Core Data Protection. The key ring is
/// persisted out-of-process in prod (blob storage) so a restart does not rotate it and orphan every
/// stored secret; DevHost/tests use the default ephemeral ring — see <c>Program.cs</c>.
/// </summary>
public sealed class ProviderSecretProtector : IProviderSecretProtector
{
    // A versioned, isolated purpose string: rotating it (…v2) invalidates old ciphertext deliberately,
    // and it keeps these secrets in their own protection scope, unreadable by any other protector.
    private const string Purpose = "Agent42.ModelProviders.ApiKey.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<ProviderSecretProtector> _logger;

    public ProviderSecretProtector(IDataProtectionProvider provider, ILogger<ProviderSecretProtector> logger)
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
            // The key ring rotated (an ephemeral ring after a prod restart, or a purpose bump) or the
            // ciphertext is corrupt — degrade to "no key" so the provider is skipped / falls back to
            // baseline, never taking resolution down (ADR 0018).
            _logger.LogWarning(ex,
                "A stored provider secret could not be decrypted; treating it as unset. If this "
                + "persists after a restart, the Data Protection key ring is not being persisted.");
            return null;
        }
    }
}
