using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Document RAG (ADR 0007 + 0008), grain side: registers an ingested document on the conversation (which rebuilds
/// the agent) and holds the shared history filter. The retrieval provider and the document-awareness instructions are
/// <see cref="DocumentContext"/>.
/// </summary>
public sealed partial class AgentGrain
{
    // Keeps only messages that should be persisted to chat history: excludes the excerpts the
    // TextSearchProvider injects (AgentRequestMessageSourceType.AIContextProvider) and messages replayed
    // from history (ChatHistory) — the framework's own default only excludes the latter. See ChatHistoryProvider
    // wiring in BuildAgent and the Step 1 spike for why persisting the injected excerpts breaks serialization.
    //
    // It also drops the research loop's own feedback messages (ADR 0082). EXTENDED, never replaced: a filter that
    // forgets the ChatHistory exclusion makes every loop re-invocation store the original question and the prior
    // answer a second time (measured).
    private static readonly Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>> ExcludeInjectedContextMessages =
        messages => messages.Where(m => !IsInjectedOrReplayed(m) && !IsResearchLoopFeedback(m));

    private static bool IsInjectedOrReplayed(ChatMessage message)
    {
        if (message.AdditionalProperties is not null
            && message.AdditionalProperties.TryGetValue(
                AgentRequestMessageSourceAttribution.AdditionalPropertiesKey, out var value)
            && value is AgentRequestMessageSourceAttribution attribution)
        {
            return attribution.SourceType == AgentRequestMessageSourceType.AIContextProvider
                || attribution.SourceType == AgentRequestMessageSourceType.ChatHistory;
        }

        return false;
    }

    public async Task RegisterDocumentAsync(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || _state.State.DocumentNames.Contains(fileName))
        {
            return;
        }

        // A copilot chat has no retrieval (AgentGrain.Copilot.cs), so a document there would be named in no
        // instructions and searched by nothing. The upload and agent-run endpoints refuse it before ingesting; this
        // is the grain's own line of defence.
        if (AgentKinds.IsCopilot(_state.State.AgentKind))
        {
            _logger.LogWarning("Agent grain {GrainKey} ignored document '{FileName}': a copilot chat has no documents",
                this.GetPrimaryKeyString(), fileName);
            return;
        }

        _state.State.DocumentNames.Add(fileName);

        // Rebuild the agent so the updated document list reaches the model as instructions —
        // same snapshot→rebuild→restore shape as BindAgentAsync and the model-switch path.
        var snapshot = await _agent.SerializeSessionAsync(_session);
        _agent = BuildAgent(_modelId);
        _session = await _agent.DeserializeSessionAsync(snapshot);

        await _state.WriteStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} registered document '{FileName}' ({Count} total)",
            this.GetPrimaryKeyString(), fileName, _state.State.DocumentNames.Count);
    }
}
