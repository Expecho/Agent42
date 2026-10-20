namespace Agent42.Agents;

/// <summary>
/// The one GitHub Copilot provider a user can have (ADR 0108): Copilot agents running on the user's own Copilot
/// subscription, signed in with GitHub. It lives in the overlay like any provider (ADR 0018), under a RESERVED key,
/// and only the GitHub sign-in callback writes it (<see cref="IUserProviderConfigGrain.ConnectGitHubCopilotAsync"/>).
/// <see cref="Kind"/> is the Backend's <c>ProviderKind.GitHubCopilot</c> by name (this layer cannot see the enum;
/// a test pins the two together).
/// </summary>
public static class GitHubCopilotProvider
{
    /// <summary>The reserved provider key, so its models are <c>github-copilot:&lt;model&gt;</c>.</summary>
    public const string Key = "github-copilot";

    /// <summary>The stored kind, the name of <c>ProviderKind.GitHubCopilot</c>.</summary>
    public const string Kind = "GitHubCopilot";

    public const string DisplayName = "GitHub Copilot";

    /// <summary>
    /// Whether a write names the reserved key or the kind. The generic provider API refuses both, so the token can't
    /// be converted into another kind's key (and sent to an endpoint the user picks), and the key can't be squatted.
    /// </summary>
    public static bool IsReserved(string? key, string? kind)
        => string.Equals(key?.Trim(), Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind?.Trim(), Kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a qualified model id names this provider, connected or not.</summary>
    public static bool IsGitHubModel(string? modelId)
        => modelId is not null && modelId.StartsWith(Key + ":", StringComparison.OrdinalIgnoreCase);
}
