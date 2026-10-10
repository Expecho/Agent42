using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Agent42.Backend;

/// <summary>
/// Encrypts and decrypts the outbound webhook's signing secret (ADR 0094). The endpoint protects it before it reaches
/// the webhook grain, so the grain only ever stores ciphertext, and <see cref="HttpAgentWebhookSender"/> unprotects it
/// at send time. It cannot be hashed like the ADR 0022 API key: HMAC needs the raw secret on every send.
///
/// <para>A sibling of <see cref="IMcpSecretProtector"/>, not a reuse of it or of the provider protector: its own
/// Data Protection scope, so its key rotation is independent of theirs.</para>
/// </summary>
public interface IWebhookSecretProtector
{
    /// <summary>Encrypts a plaintext secret for persistence.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored ciphertext, or returns <c>null</c> when it is blank or undecryptable (a rotated key ring).
    /// Never throws. The sender then records <see cref="Agents.WebhookLimits.SecretUnavailableError"/> and sends
    /// nothing: a delivery is never sent unsigned.
    /// </summary>
    string? Unprotect(string? ciphertext);
}

/// <summary><see cref="IWebhookSecretProtector"/> over ASP.NET Core Data Protection.</summary>
public sealed class WebhookSecretProtector : IWebhookSecretProtector
{
    // Versioned and isolated, like the MCP purpose: bumping it (…v2) invalidates old ciphertext deliberately.
    private const string Purpose = "Agent42.AgentWebhooks.Secret.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<WebhookSecretProtector> _logger;

    public WebhookSecretProtector(IDataProtectionProvider provider, ILogger<WebhookSecretProtector> logger)
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
                "A stored webhook secret could not be decrypted; nothing will be sent until the owner rotates it. If "
                + "this persists after a restart, the Data Protection key ring is not being persisted.");
            return null;
        }
    }
}
