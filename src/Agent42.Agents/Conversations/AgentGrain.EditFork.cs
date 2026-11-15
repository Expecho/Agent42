using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Edit the last message, regenerate the last reply, and fork a conversation (ADR 0092): the grain side, which owns
/// the refusals, the persist pipeline and the session binding. The removal, the fork payload and the blob copies are
/// in <see cref="ConversationForking"/> (ADR 0106), whose remarks hold the whole-turn and copy rules.
///
/// <para>None of these is a model run, so none creates a <see cref="TurnContext"/>.
/// All three refuse while a run is in flight (<c>_runsInFlight</c>), checked BEFORE the token: a running research
/// turn is hidden by <c>VisibleHistory</c>, so a count taken during one would compare against the wrong history.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>This activation's edit/fork helpers, built in <c>OnActivateAsync</c> beside the search projection.</summary>
    private IConversationForking _forking = null!;

    public async Task<HistoryEditOutcome> TruncateLastTurnAsync(
        int expectedUserTurnCount, string expectedLastUserTextHash, bool ephemeral)
    {
        var projected = ProjectHistory();
        if (HistoryEditRefusal(projected, expectedUserTurnCount, expectedLastUserTextHash) is { } refusal)
        {
            return refusal;
        }

        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return HistoryEditOutcome.Nothing;
        }

        // The LAST SESSION user turn must be the last visible one. A blank last user message (it does not project)
        // means the client cannot see the turn it would be replacing, so nothing is removed.
        var userIndices = HistoryRetention.UserTurnIndices(messages);
        var lastVisibleUser = projected.LastOrDefault(p => p.Turn.Role == ChatRole.User.Value);
        if (lastVisibleUser is null || lastVisibleUser.SessionUserTurn != userIndices.Count - 1)
        {
            return HistoryEditOutcome.Nothing;
        }

        // After every other refusal and before anything is removed: the re-run that follows would otherwise hit the
        // no-model early exit with the old turn already gone.
        if (!await HasModelAsync())
        {
            return HistoryEditOutcome.NoModel;
        }

        _forking.RemoveLastTurn(
            new EditContext(messages, _state.State.TurnAttachments, _state.State.GeneratedImages, _state.State.Artifacts),
            userIndices);
        var projectedCount = ProjectHistory().Count;

        // In memory only for a conversation that has never been written (ADR 0034). The flag comes from the client,
        // and a conversation that HAS a stored record must not be left with the removed turn in Cosmos and its index
        // documents behind, whatever the request says. In memory only means no write and no index call: the search
        // cursor is pulled back instead, so the next turn's ordinals start where the removed turn's did.
        var inMemoryOnly = ephemeral && _state.State.SerializedSession is not { Length: > 0 };
        _search.TruncateTo(projectedCount, pullBackCursor: inMemoryOnly);
        if (!inMemoryOnly)
        {
            await PersistTruncationAsync();
        }

        _logger.LogInformation(
            "Agent grain {GrainKey} removed its last turn for an edit or regenerate (ephemeral: {Ephemeral})",
            this.GetPrimaryKeyString(), ephemeral);
        return HistoryEditOutcome.Ok;
    }

    public async Task<ConversationForkResult> CreateForkAsync(
        int throughUserTurn, int expectedUserTurnCount, string expectedLastUserTextHash)
    {
        var projected = ProjectHistory();
        if (HistoryEditRefusal(projected, expectedUserTurnCount, expectedLastUserTextHash) is { } refusal)
        {
            return new ConversationForkResult(refusal);
        }

        // Incognito turns still in memory (ADR 0034) must never reach a persisted, indexed fork. The route's "listed
        // source" check keeps incognito chats out; this covers a listed chat that also served an incognito turn.
        if (_search.HasEphemeralTurns)
        {
            return new ConversationForkResult(HistoryEditOutcome.Unsupported);
        }

        var visibleUsers = projected.Where(p => p.Turn.Role == ChatRole.User.Value).ToList();
        if (throughUserTurn < 0 || throughUserTurn >= visibleUsers.Count)
        {
            return new ConversationForkResult(HistoryEditOutcome.Nothing);
        }

        // A COPY of the session, never _session itself: the source must stay exactly as it is.
        var copy = await _agent.DeserializeSessionAsync(await _agent.SerializeSessionAsync(_session));
        if (!copy.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return new ConversationForkResult(HistoryEditOutcome.Nothing);
        }

        // The cut is the session user message that begins the NEXT exchange, whether or not that message projects,
        // so a blank user turn between visible ones never shifts the boundary.
        var keptUserTurns = visibleUsers[throughUserTurn].SessionUserTurn + 1;
        var userIndices = HistoryRetention.UserTurnIndices(messages);
        var cut = keptUserTurns < userIndices.Count ? userIndices[keptUserTurns] : messages.Count;

        var excluded = messages.GetRange(cut, messages.Count - cut);
        messages.RemoveRange(cut, messages.Count - cut);

        var fork = new ConversationFork(
            SourceOwnerKey: this.GetPrimaryKeyString(),
            AgentId: _state.State.AgentId,
            AgentName: _state.State.AgentName,
            SystemPrompt: _state.State.SystemPrompt,
            AgentKind: _state.State.AgentKind,
            DeploymentName: string.IsNullOrEmpty(_modelId) ? _state.State.DeploymentName : _modelId,
            EnabledTools: _state.State.EnabledTools is { } tools ? [.. tools] : null,
            SerializedSession: JsonSerializer.Serialize(await _agent.SerializeSessionAsync(copy)),
            TurnAttachments: [.. _state.State.TurnAttachments.Take(keptUserTurns).Select(refs => refs.ToList())],
            GeneratedImages: _forking.KeptGeneratedImages(messages, _state.State.GeneratedImages),
            Artifacts: _forking.ArtifactsWithout(
                _state.State.Artifacts, ConversationForking.ArtifactVersionsCreatedIn(excluded)));

        return new ConversationForkResult(HistoryEditOutcome.Ok, fork);
    }

    public async Task<bool> InitializeFromForkAsync(ConversationFork fork)
    {
        // Only a brand-new grain: a fork must never overwrite a conversation or rebind a persona (ADR 0003).
        if (_state.State.SerializedSession is { Length: > 0 } || !string.IsNullOrEmpty(_state.State.AgentId))
        {
            return false;
        }

        var ownerKey = this.GetPrimaryKeyString();
        var copied = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);

        _state.State.AgentId = fork.AgentId;
        _state.State.AgentName = fork.AgentName;
        _state.State.SystemPrompt = fork.SystemPrompt;
        _state.State.AgentKind = fork.AgentKind;
        _state.State.DeploymentName = fork.DeploymentName;
        _state.State.EnabledTools = fork.EnabledTools is { } tools ? [.. tools] : null;

        var turnAttachments = new List<List<AttachmentRef>>();
        foreach (var refs in fork.TurnAttachments)
        {
            var turn = new List<AttachmentRef>();
            foreach (var reference in refs)
            {
                turn.Add(await _forking.CopyForkBlobAsync(reference, ownerKey, copied));
            }

            turnAttachments.Add(turn);
        }

        _state.State.TurnAttachments = turnAttachments;

        var images = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);
        foreach (var (imageId, reference) in fork.GeneratedImages)
        {
            images[imageId] = await _forking.CopyForkBlobAsync(reference, ownerKey, copied);
        }

        _state.State.GeneratedImages = images;
        _state.State.Artifacts = await _forking.CopyForkArtifactsAsync(fork, ownerKey);

        await BindForkedSessionAsync(fork.SerializedSession);

        CompactToolPayloads();
        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        await _state.WriteStateAsync();
        await _search.IndexNewTurnsAsync();

        _logger.LogInformation(
            "Agent grain {GrainKey} initialized from a fork of {Source}", ownerKey, fork.SourceOwnerKey);
        return true;
    }

    // The refusals all three operations share (ADR 0092 D9), in this order: a run in flight first (see the class
    // remarks), then a Copilot chat, then a stale token. Null when the request may proceed.
    private HistoryEditOutcome? HistoryEditRefusal(
        List<ProjectedTurn> projected, int expectedUserTurnCount, string? expectedLastUserTextHash)
    {
        if (_runsInFlight > 0)
        {
            return HistoryEditOutcome.Busy;
        }

        if (AgentKinds.IsCopilot(_state.State.AgentKind))
        {
            return HistoryEditOutcome.Unsupported;
        }

        var visibleUserTexts = projected.Where(p => p.Turn.Role == ChatRole.User.Value).Select(p => p.Turn.Text).ToList();
        return ConversationForking.TokenMatches(visibleUserTexts, expectedUserTurnCount, expectedLastUserTextHash)
            ? null
            : HistoryEditOutcome.Stale;
    }

    // Whether the re-run after a truncation can reach a model: the same re-resolve the chat path does for a user
    // who added their first provider after this grain activated (ADR 0019). The rebind itself is left to the run.
    private async Task<bool> HasModelAsync()
    {
        if (!string.IsNullOrEmpty(_modelId))
        {
            return true;
        }

        _chatClientFactory = await _chatClientFactoryProvider.GetForUserAsync(_userId);
        return !string.IsNullOrEmpty(_chatClientFactory.DefaultModelId);
    }

    // The persist-site pipeline (ChatCoreAsync's order), with the reconcile forced: the removed turn's index
    // documents can only go by dropping the conversation's and re-projecting, since there is no delete-by-ordinal.
    private async Task PersistTruncationAsync()
    {
        CompactToolPayloads();
        ApplyRetention();
        ReconcileMcpApps();

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        // No run is in flight (refused above), so this write carries any pending shared-state edit too.
        _sharedStateDirty = false;
        await _state.WriteStateAsync();

        await _search.ReconcileAfterPruneAsync(pruned: true);
        await _search.IndexNewTurnsAsync();
    }

    // BindAgentAsync's rebuild shape for the forked session: resolve the model with OnActivateAsync's empty-factory
    // rule, rebuild the agent for the copied persona, load the session, and strip tool history for a no-tools model.
    private async Task BindForkedSessionAsync(string serializedSession)
    {
        _modelId = string.IsNullOrEmpty(_chatClientFactory.DefaultModelId)
            ? ""
            : (_state.State.DeploymentName ?? _chatClientFactory.DefaultModelId);
        _agent = BuildAgent(_modelId);
        _session = await _agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(serializedSession));

        if (!string.IsNullOrEmpty(_modelId) && !_chatClientFactory.GetCapabilities(_modelId).SupportsTools)
        {
            StripToolHistory();
        }
    }
}
