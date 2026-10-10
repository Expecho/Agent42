using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Edit the last message, regenerate the last reply, and fork a conversation (ADR 0092).
///
/// <para><b>Edit and regenerate truncate the last WHOLE session turn in place</b> — the user message and everything
/// after it — the tail mirror of the retention prune in <c>AgentGrain.Retention.cs</c>, which mutates the same
/// <c>TryGetInMemoryChatHistory</c> list. Whole turns only, so a <see cref="FunctionCallContent"/> never loses its
/// <see cref="FunctionResultContent"/>. Everything the session does not hold is maintained by hand:
/// <c>TurnAttachments</c> (positional), <c>GeneratedImages</c>, the artifact versions the removed messages created,
/// the incognito ordinals and the search index.</para>
///
/// <para><b>A fork never touches the source.</b> It cuts a COPY of the session after a whole exchange, and the new
/// grain copies every blob it keeps under its own owner key, because the attachment and artifact proxies resolve a
/// blob by <c>(ownerKey, id)</c>.</para>
///
/// <para>None of these is a model run, so none sets a turn-scoped field and the rule 6 reset ritual is untouched.
/// All three refuse while a run is in flight (<c>_runsInFlight</c>), checked BEFORE the token: a running research
/// turn is hidden by <c>VisibleHistory</c>, so a count taken during one would compare against the wrong history.</para>
/// </summary>
public sealed partial class AgentGrain
{
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
        var userIndices = UserTurnIndices(messages);
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

        RemoveLastTurn(messages, userIndices);
        var projectedCount = ProjectHistory().Count;
        _ephemeralOrdinals.RemoveWhere(ordinal => ordinal >= projectedCount);

        // In memory only for a conversation that has never been written (ADR 0034). The flag comes from the client,
        // and a conversation that HAS a stored record must not be left with the removed turn in Cosmos and its index
        // documents behind, whatever the request says.
        if (ephemeral && _state.State.SerializedSession is not { Length: > 0 })
        {
            // In memory only (ADR 0034): no write and no index call. The cursor is pulled back so the next turn's
            // ordinals start where the removed turn's did.
            _indexedTurnCount = Math.Min(_indexedTurnCount, projectedCount);
        }
        else
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
        if (_ephemeralOrdinals.Count > 0)
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
        var userIndices = UserTurnIndices(messages);
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
            GeneratedImages: KeptGeneratedImages(messages),
            Artifacts: ArtifactsWithout(ArtifactVersionsCreatedIn(excluded)));

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
                turn.Add(await CopyForkBlobAsync(reference, ownerKey, copied));
            }

            turnAttachments.Add(turn);
        }

        _state.State.TurnAttachments = turnAttachments;

        var images = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);
        foreach (var (imageId, reference) in fork.GeneratedImages)
        {
            images[imageId] = await CopyForkBlobAsync(reference, ownerKey, copied);
        }

        _state.State.GeneratedImages = images;
        _state.State.Artifacts = await CopyForkArtifactsAsync(fork, ownerKey);

        await BindForkedSessionAsync(fork.SerializedSession);

        CompactArtifactCalls();
        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        await _state.WriteStateAsync();
        await IndexNewTurnsForSearchAsync();

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

        return TokenMatches(projected, expectedUserTurnCount, expectedLastUserTextHash)
            ? null
            : HistoryEditOutcome.Stale;
    }

    // The concurrency token: how many user turns the client saw, AND the hash of the last one's text. A count alone
    // can be replayed — a second tab loaded at count N, sending after an edit restored the count to N, would
    // silently replace the freshly generated turn. The hash makes the token an identity.
    private static bool TokenMatches(
        List<ProjectedTurn> projected, int expectedUserTurnCount, string? expectedLastUserTextHash)
    {
        var users = projected.Where(p => p.Turn.Role == ChatRole.User.Value).ToList();
        var hash = users.Count == 0 ? HistoryTextHash("") : HistoryTextHash(users[^1].Turn.Text);
        return users.Count == expectedUserTurnCount
            && string.Equals(hash, expectedLastUserTextHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hex SHA-256 of a turn's text as UTF-8: the same value the browser computes with
    /// <c>crypto.subtle.digest</c> for the concurrency token.
    /// </summary>
    private static string HistoryTextHash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

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

    // Removes the last whole session turn and everything that indexes off it.
    private void RemoveLastTurn(List<ChatMessage> messages, List<int> userIndices)
    {
        var start = userIndices[^1];
        var removed = messages.GetRange(start, messages.Count - start);

        foreach (var message in removed)
        {
            if (AssistantImageId(message) is { } imageId)
            {
                _state.State.GeneratedImages.Remove(imageId);
            }
        }

        foreach (var (artifactId, version) in ArtifactVersionsCreatedIn(removed))
        {
            RemoveArtifactVersion(_state.State.Artifacts, artifactId, version);
        }

        messages.RemoveRange(start, messages.Count - start);

        // TRUNCATED to the remaining user-turn count, never "remove the last entry": BuildUserTurnAsync appends a
        // turn's entry before its run, so a run that failed without storing its user message leaves an orphan
        // trailing entry, and dropping only the last one would keep the real turn's refs and mis-join the next one.
        var remaining = userIndices.Count - 1;
        if (_state.State.TurnAttachments.Count > remaining)
        {
            _state.State.TurnAttachments.RemoveRange(remaining, _state.State.TurnAttachments.Count - remaining);
        }
    }

    // The persist-site pipeline (ChatCoreAsync's order), with the reconcile forced: the removed turn's index
    // documents can only go by dropping the conversation's and re-projecting, since there is no delete-by-ordinal.
    private async Task PersistTruncationAsync()
    {
        CompactArtifactCalls();
        await ApplyRetentionAsync();

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        // No run is in flight (refused above), so this write carries any pending shared-state edit too.
        _sharedStateDirty = false;
        await _state.WriteStateAsync();

        await ReconcileSearchIndexAfterPruneAsync(pruned: true);
        await IndexNewTurnsForSearchAsync();
    }

    // Every (artifactId, version) that a create_artifact or update_artifact call in these messages stored. Taken
    // from the tool RESULTS, joined to their calls by CallId (a result arrives on a later message than its call).
    // A refused call returns plain text and contributes nothing.
    private static List<(string ArtifactId, int Version)> ArtifactVersionsCreatedIn(List<ChatMessage> messages)
    {
        var calls = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(c => c.Name is CreateArtifactTool or UpdateArtifactTool)
            .Select(c => c.CallId)
            .ToHashSet(StringComparer.Ordinal);

        return [.. messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Where(r => calls.Contains(r.CallId))
            .Select(r => ParseArtifactResult(r.Result))
            .OfType<ArtifactInfo>()
            .Select(info => (info.ArtifactId, info.Version))];
    }

    // Drops one version, and the record with it once it has none left. Its blob is orphaned (the purge gap).
    private static void RemoveArtifactVersion(Dictionary<string, ArtifactRecord> artifacts, string artifactId, int version)
    {
        if (!artifacts.TryGetValue(artifactId, out var record))
        {
            return;
        }

        record.Versions.RemoveAll(v => v.Version == version);
        if (record.Versions.Count == 0)
        {
            artifacts.Remove(artifactId);
        }
    }

    // A deep copy of this conversation's artifacts minus the given versions, for a fork payload.
    private List<ArtifactRecord> ArtifactsWithout(List<(string ArtifactId, int Version)> excluded)
    {
        var copies = _state.State.Artifacts.Values.ToDictionary(
            a => a.Id,
            a => new ArtifactRecord
            {
                Id = a.Id,
                Kind = a.Kind,
                Title = a.Title,
                Language = a.Language,
                CreatedAt = a.CreatedAt,
                Versions = [.. a.Versions],
            },
            StringComparer.Ordinal);

        foreach (var (artifactId, version) in excluded)
        {
            RemoveArtifactVersion(copies, artifactId, version);
        }

        return [.. copies.Values];
    }

    // The generated images the kept messages still reference (they are keyed by a message-level id, ADR 0020).
    private Dictionary<string, AttachmentRef> KeptGeneratedImages(List<ChatMessage> kept)
    {
        var images = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);
        foreach (var message in kept)
        {
            if (AssistantImageId(message) is { } id && _state.State.GeneratedImages.TryGetValue(id, out var reference))
            {
                images[id] = reference;
            }
        }

        return images;
    }

    // One blob copied under this conversation's key, once per id however often the payload names it. A blob that
    // cannot be copied keeps its id under the new key and is logged: one missing image must not lose the fork, and
    // the proxy then answers 404 for it exactly as the source's would.
    private async Task<AttachmentRef> CopyForkBlobAsync(
        AttachmentRef source, string ownerKey, Dictionary<string, AttachmentRef> copied)
    {
        if (copied.TryGetValue(source.Id, out var done))
        {
            return done;
        }

        AttachmentRef copy;
        try
        {
            copy = await _attachments.CopyAsync(source, ownerKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent grain {GrainKey} could not copy attachment {AttachmentId} into a fork",
                ownerKey, source.Id);
            copy = source with { BlobPath = $"{ownerKey}/{source.Id}" };
        }

        copied[source.Id] = copy;
        return copy;
    }

    // The fork's artifacts with every version's blob copied. A version holds only an attachment id, so its ref is
    // looked up under the SOURCE key first; a version whose blob is missing or cannot be copied is dropped and
    // logged, never fatal — the same rule CopyForkBlobAsync applies to images.
    private async Task<Dictionary<string, ArtifactRecord>> CopyForkArtifactsAsync(ConversationFork fork, string ownerKey)
    {
        var artifacts = new Dictionary<string, ArtifactRecord>(StringComparer.Ordinal);
        foreach (var record in fork.Artifacts)
        {
            var versions = new List<ArtifactVersionRef>();
            foreach (var version in record.Versions)
            {
                if (await TryCopyArtifactVersionAsync(fork.SourceOwnerKey, version, ownerKey))
                {
                    versions.Add(version);
                }
                else
                {
                    _logger.LogWarning(
                        "Agent grain {GrainKey} dropped artifact {ArtifactId} v{Version} from a fork: its blob is missing",
                        ownerKey, record.Id, version.Version);
                }
            }

            if (versions.Count > 0)
            {
                record.Versions = versions;
                artifacts[record.Id] = record;
            }
        }

        return artifacts;
    }

    private async Task<bool> TryCopyArtifactVersionAsync(string sourceOwnerKey, ArtifactVersionRef version, string ownerKey)
    {
        try
        {
            if (await _attachments.GetAsync(sourceOwnerKey, version.AttachmentId) is not { } source)
            {
                return false;
            }

            await _attachments.CopyAsync(source, ownerKey);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent grain {GrainKey} could not copy artifact blob {AttachmentId} into a fork",
                ownerKey, version.AttachmentId);
            return false;
        }
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
