using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// What a model can be asked to do. Not every model behind every provider supports the same
/// things — GitHub Models publishes text-only models alongside vision ones, and some reject a
/// request that declares tools at all (ADR 0013) — so the grain gates on this rather than
/// assuming the Azure OpenAI feature set.
/// </summary>
/// <param name="SupportsTools">Whether the model accepts a function-tool schema at all.</param>
/// <param name="SupportsVision">Whether the model can read an image supplied as a URL.</param>
/// <param name="SupportsHostedTools">
/// Whether the model reaches OpenAI's <b>hosted</b> server-side tools (web search, code interpreter).
/// True only for a Foundry provider running the <b>Responses</b> API (ADR 0014) — Chat Completions
/// 400s on them, which is the whole reason the self-hosted SearXNG shims exist (ADR 0011). When set,
/// the grain swaps its <c>web_search</c>/<c>fetch_web_page</c> shims for the native hosted tools.
/// </param>
/// <param name="SupportsImageGeneration">
/// Whether picking this model turns a turn into <b>image generation</b> instead of a chat reply (ADR 0020):
/// a text-to-image / image-to-image model (an OpenAI-compatible <c>/v1/images</c> endpoint). Opt-in and
/// declared per model — <see cref="Full"/> stays chat-only, and a discovered Foundry deployment is chat
/// (<c>false</c>) — because it is orthogonal to chat: an image model runs a different grain path entirely.
/// </param>
public record ModelCapabilities(
    bool SupportsTools, bool SupportsVision, bool SupportsHostedTools = false, bool SupportsImageGeneration = false)
{
    /// <summary>A model with no known restrictions — the assumption for a discovered Foundry deployment.</summary>
    public static ModelCapabilities Full => new(SupportsTools: true, SupportsVision: true);
}

/// <summary>
/// Resolves an <see cref="IChatClient"/> for a specific model. The agent grain uses this so a
/// single conversation can switch between the models offered by any configured provider, rather
/// than being pinned to one client.
///
/// <para>A model is named by a <b>qualified id</b> — <c>provider:model</c>, split on the FIRST
/// colon, because a provider's own model name may itself contain a <c>/</c> (GitHub Models ids look
/// like <c>openai/gpt-4o-mini</c>). An id with no colon is a legacy, unqualified id and resolves
/// against the default provider, which is what keeps conversations persisted before ADR 0013
/// working with no state migration.</para>
/// </summary>
public interface IChatClientFactory
{
    /// <summary>The qualified model id used when a conversation hasn't picked one yet.</summary>
    string DefaultModelId { get; }

    /// <summary>
    /// Returns a chat client bound to <paramref name="modelId"/>, or to the default model when it
    /// is null or blank. An id naming an unknown provider falls back to the default rather than
    /// throwing — a conversation must never become unopenable because a provider left the config.
    /// </summary>
    IChatClient GetClient(string? modelId);

    /// <summary>Capabilities of <paramref name="modelId"/>, or of the default model when null/blank.</summary>
    ModelCapabilities GetCapabilities(string? modelId);

    /// <summary>
    /// The image generator for <paramref name="modelId"/> (ADR 0020), or <c>null</c> when that model is not an
    /// image-generation model (<see cref="ModelCapabilities.SupportsImageGeneration"/> is false). Non-null only
    /// for a declared OpenAI-compatible image model; a chat model, a Foundry deployment, and the no-provider
    /// state all return <c>null</c>. This is how the grain decides a turn produces an image, not a chat reply.
    /// </summary>
    IImageGenerationService? GetImageGenerationService(string? modelId);
}

/// <summary>
/// An <see cref="IChatClientFactory"/> that always hands back the same client regardless of the
/// requested model. Used by local dev (DevHost) and tests, where a single canned/fake client
/// stands in for a real provider.
///
/// <para><paramref name="capabilities"/> exists so a test can register a model that does NOT do
/// tools or vision: this is the only factory the grain tests get, so without it the degraded paths
/// the grain now has (no tools, no skills provider, no image content) would be untestable.</para>
/// </summary>
public sealed class SingleChatClientFactory(
    IChatClient client,
    string defaultModelId = "default",
    Func<string?, ModelCapabilities>? capabilities = null,
    IImageGenerationService? imageGenerator = null) : IChatClientFactory
{
    public string DefaultModelId { get; } = defaultModelId;

    public IChatClient GetClient(string? modelId) => client;

    public ModelCapabilities GetCapabilities(string? modelId)
        => capabilities?.Invoke(modelId) ?? ModelCapabilities.Full;

    // The injected generator is handed back only for a model the capabilities func flags as image-generation
    // (ADR 0020) — so DevHost/tests select an image model to reach it, and a chat model gets null, exactly
    // like the multi-provider factory. Null generator (the default) → always null.
    public IImageGenerationService? GetImageGenerationService(string? modelId)
        => GetCapabilities(modelId).SupportsImageGeneration ? imageGenerator : null;
}
