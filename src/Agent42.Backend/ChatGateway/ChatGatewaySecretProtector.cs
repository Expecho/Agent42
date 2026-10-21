using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Agent42.Backend;

/// <summary>
/// Encrypts and decrypts a chat bot's platform credentials (ADR 0111). The management endpoint protects them before
/// they reach <c>UserChatConnectionsGrain</c>, so the grain only ever holds ciphertext, and the gateway unprotects them
/// when it opens a session. A sibling of <see cref="IWebhookSecretProtector"/>: its own Data Protection purpose, so the
/// ciphertext families cannot be swapped and rotate independently.
/// </summary>
public interface IChatGatewaySecretProtector
{
    string Protect(ChatCredentials credentials);

    /// <summary>
    /// The credentials in <paramref name="ciphertext"/>, or <c>null</c> when it is blank or undecryptable (a rotated
    /// key ring). Never throws: the gateway then faults the connection and asks the owner to paste them again.
    /// </summary>
    ChatCredentials? Unprotect(string? ciphertext);
}

/// <summary><see cref="IChatGatewaySecretProtector"/> over ASP.NET Core Data Protection.</summary>
public sealed class ChatGatewaySecretProtector : IChatGatewaySecretProtector
{
    // Versioned and isolated, like the MCP and webhook purposes: bumping it (…v2) invalidates old ciphertext deliberately.
    private const string Purpose = "Agent42.ChatGateway.Secret.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<ChatGatewaySecretProtector> _logger;

    public ChatGatewaySecretProtector(IDataProtectionProvider provider, ILogger<ChatGatewaySecretProtector> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(ChatCredentials credentials) => _protector.Protect(JsonSerializer.Serialize(credentials));

    public ChatCredentials? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChatCredentials>(_protector.Unprotect(ciphertext));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.LogWarning(ex,
                "Stored chat bot credentials could not be decrypted; the connection stays stopped until the owner pastes "
                + "them again. If this persists after a restart, the Data Protection key ring is not being persisted.");
            return null;
        }
    }
}
