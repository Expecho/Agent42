using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// What a model can be asked to do. Not every model behind every provider supports the same
/// things — GitHub Models publishes text-only models alongside vision ones, and some reject a
/// request that declares tools at all (ADR 0013) — so the grain gates on this rather than
/// assuming the Azure OpenAI feature set.
/// </summary>
public record ModelCapabilities(bool SupportsTools, bool SupportsVision)
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
    Func<string?, ModelCapabilities>? capabilities = null) : IChatClientFactory
{
    public string DefaultModelId { get; } = defaultModelId;

    public IChatClient GetClient(string? modelId) => client;

    public ModelCapabilities GetCapabilities(string? modelId)
        => capabilities?.Invoke(modelId) ?? ModelCapabilities.Full;
}
