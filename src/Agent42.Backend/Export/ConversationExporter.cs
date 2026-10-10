using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Gathers one conversation for export from the grain methods that already exist (ADR 0091) — no export-specific
/// grain method. Blob bytes are not read here: <see cref="ConversationZipWriter"/> opens them one at a time.
/// </summary>
public static class ConversationExporter
{
    /// <summary>
    /// The caller's listed conversation with this id, or null. The index is the gate for every export route: an
    /// ephemeral chat is never touched into it (ADR 0034) and an id the user never used is not in it, so a null here
    /// means 404 before any <see cref="IAgentGrain"/> is activated.
    /// </summary>
    public static async Task<ConversationInfo?> FindListedAsync(IGrainFactory grains, string userId, string conversationId)
        => (await grains.GetGrain<IConversationIndexGrain>(userId).GetConversationsAsync())
            .FirstOrDefault(c => string.Equals(c.Id, conversationId, StringComparison.Ordinal));

    public static async Task<ConversationExport> GatherAsync(IGrainFactory grains, string ownerKey, ConversationInfo conversation)
    {
        var agent = grains.GetGrain<IAgentGrain>(ownerKey);
        var turns = await agent.GetHistoryAsync();
        var artifacts = await agent.GetArtifactsAsync();
        var binding = await agent.GetAgentBindingAsync();
        var model = await agent.GetSelectedModelAsync();
        var sharedState = await agent.GetSharedStateAsync();
        return new ConversationExport(conversation, model, binding, sharedState.Json, turns, artifacts);
    }
}
