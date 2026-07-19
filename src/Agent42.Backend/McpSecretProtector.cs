using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Agent42.Backend;

/// <summary>
/// Encrypts and decrypts user-configured MCP-server secrets — the static Header value, and (Phases 2/3)
/// the MSAL client secret and OAuth tokens. The API layer protects a secret before it reaches the overlay
/// grain (so the grain only ever stores ciphertext), and the tool-provider layer unprotects it only when
/// building a connection.
///
/// <para>Distinct from <see cref="IProviderSecretProtector"/> on purpose: MCP secrets are a separate
/// concern with their own Data-Protection scope, so their key rotation is independent of model-provider
/// keys. (The provider protector's purpose is a private const, not a parameter, so it cannot be reused
/// with a distinct scope — hence a dedicated protector.)</para>
/// </summary>
public interface IMcpSecretProtector
{
    /// <summary>Encrypts a plaintext secret for persistence.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored ciphertext, or returns <c>null</c> when it is blank or undecryptable (a rotated
    /// key ring). Never throws: an MCP overlay is best-effort, so an unreadable secret degrades the server
    /// to "no auth secret" (and it is skipped when auth needs one) rather than bricking the tool set.
    /// </summary>
    string? Unprotect(string? ciphertext);
}

/// <summary>
/// <see cref="IMcpSecretProtector"/> over ASP.NET Core Data Protection. The key ring is persisted
/// out-of-process in prod (blob storage) so a restart does not rotate it and orphan every stored secret;
/// DevHost/tests use the default ephemeral ring — see <c>Program.cs</c>.
/// </summary>
public sealed class McpSecretProtector : IMcpSecretProtector
{
    // A versioned, isolated purpose string: rotating it (…v2) invalidates old ciphertext deliberately,
    // and it keeps MCP secrets in their own protection scope, unreadable by any other protector.
    private const string Purpose = "Agent42.Mcp.Secret.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<McpSecretProtector> _logger;

    public McpSecretProtector(IDataProtectionProvider provider, ILogger<McpSecretProtector> logger)
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
            // ciphertext is corrupt — degrade to "no secret" so the server is skipped, never taking the
            // tool set down.
            _logger.LogWarning(ex,
                "A stored MCP secret could not be decrypted; treating it as unset. If this persists "
                + "after a restart, the Data Protection key ring is not being persisted.");
            return null;
        }
    }
}
