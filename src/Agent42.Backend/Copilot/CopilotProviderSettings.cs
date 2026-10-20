namespace Agent42.Backend;

/// <summary>
/// How the Copilot runtime reaches one of the user's providers (BYOK, ADR 0018): the runtime's
/// <c>ProviderConfig</c> without the SDK type, so <see cref="MultiProviderChatClientFactory"/> can carry it without
/// referencing the SDK.
///
/// <para><b>It holds a decrypted API key.</b> It lives only in the per-user factory (in memory, like the clients
/// built from the same key) and in the <c>SessionConfig</c> handed to the runtime. It is never put in grain state, and
/// <see cref="ToString"/> is overridden because a record's generated one prints every property, which would put the
/// key in any log line that interpolates it.</para>
/// </summary>
/// <param name="Type">The runtime's provider type. <c>"openai"</c> for both Foundry's <c>/openai/v1</c> surface and an
/// OpenAI-compatible endpoint (measured: Foundry with <c>Type = "openai"</c> and the resource key completes a turn).</param>
/// <param name="BaseUrl">The API base, from config only, never from a request (the ADR 0013 SSRF invariant).</param>
/// <param name="ApiKey">The provider key.</param>
/// <param name="WireApi"><c>"responses"</c> for a Foundry provider on the Responses API (ADR 0014), else
/// <c>"completions"</c>, the runtime's default.</param>
public sealed record CopilotProviderSettings(string Type, string BaseUrl, string ApiKey, string WireApi)
{
    public override string ToString() => $"CopilotProviderSettings {{ Type = {Type}, BaseUrl = {BaseUrl}, WireApi = {WireApi} }}";

    /// <summary>A Foundry (Azure OpenAI) provider, always on its GA <c>/openai/v1</c> surface.</summary>
    public static CopilotProviderSettings Foundry(Uri endpoint, string apiKey, bool useResponses)
        => new("openai", endpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/", apiKey, useResponses ? "responses" : "completions");

    /// <summary>An OpenAI-compatible provider, at its configured endpoint. No Responses surface (ADR 0014).</summary>
    public static CopilotProviderSettings OpenAICompatible(Uri endpoint, string apiKey)
        => new("openai", endpoint.AbsoluteUri, apiKey, "completions");

    /// <summary>The <see cref="Type"/> of <see cref="GitHub"/> settings, which is not a runtime provider type.</summary>
    public const string GitHubType = "github";

    /// <summary>
    /// The user's GitHub account (ADR 0108): no BYOK provider at all. <see cref="ApiKey"/> holds the GitHub token, which
    /// goes into the session's <c>GitHubToken</c>, never into <c>CopilotClientOptions</c> or the runtime's environment.
    /// </summary>
    public static CopilotProviderSettings GitHub(string token) => new(GitHubType, "", token, "");

    /// <summary>Whether these settings sign the session in with the user's GitHub account instead of BYOK.</summary>
    public bool UsesGitHubAccount => Type == GitHubType;
}

/// <summary>
/// A chat-client factory that can tell the Copilot runtime how to reach a model's provider. Implemented by
/// <see cref="MultiProviderChatClientFactory"/>. The copilot agent factory type-tests the grain's own factory
/// instance for it, so the agent runs on exactly the provider overlay the chat was activated with.
/// </summary>
public interface ICopilotProviderSource
{
    /// <summary>
    /// The provider settings and the bare model (deployment) name for <paramref name="modelId"/>, or null when no
    /// provider is configured or the provider has no Copilot mapping.
    /// </summary>
    (CopilotProviderSettings Settings, string Model)? GetCopilotProvider(string? modelId);
}
