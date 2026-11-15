using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The replay half of MCP Apps (ADR 0133). The live card is fed from the stream; to draw it again after a reload the
/// raw result is saved to a blob and a small record names it (<see cref="AgentGrainState.McpApps"/>).
///
/// <para>The lifecycle is its own design, not the artifact precedent: the list is keyed by tool call id and every
/// way a call can leave the conversation must drop its record. <see cref="StoreMcpAppsAsync"/> appends (and applies the
/// cap), <see cref="ReconcileMcpApps"/> drops the records whose call is no longer in the session (a retention prune, an
/// edit or regenerate), purge clears the list, and a fork copies none (<c>ConversationFork</c> has no apps field, so
/// the fork's records start empty and its cards show the plain chip). Blobs are orphaned, never deleted: the attachment
/// store has no delete, the same gap attachments and artifacts have.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// Saves this turn's app results to blob and records them, skipping an incognito turn (ADR 0034: the card renders
    /// live only). Called at the end-of-turn persist site BEFORE the write, so a crash in between orphans a blob rather
    /// than leaving a record that points at nothing. Best-effort per result: a failed save means that card degrades to
    /// the chip, never a failed turn.
    /// </summary>
    private async Task StoreMcpAppsAsync(TurnContext turn)
    {
        if (turn.Ephemeral)
        {
            return;
        }

        foreach (var (callId, result) in turn.AppResults)
        {
            await StoreMcpAppAsync(result, callId);
        }
    }

    private async Task StoreMcpAppAsync(McpAppResult result, string callId)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(result.ResultJson);
            var saved = await _attachments.SaveAsync(
                this.GetPrimaryKeyString(), McpAppBlobName(callId), "application/octet-stream", bytes);
            _state.State.McpApps.RemoveAll(r => r.CallId == callId);
            _state.State.McpApps.Add(new McpAppRecord(
                callId, result.App.ServerId, result.App.ToolName, result.App.ResourceUri, saved.Id, bytes.Length));
            DropOldestMcpApps();
        }
        catch (Exception ex)
        {
            // Never the result: it is whatever a third-party server returned (ADR 0010).
            _logger.LogWarning(ex, "Agent grain {GrainKey} could not store an MCP App result; its card will not replay",
                this.GetPrimaryKeyString());
        }
    }

    // Past the cap the OLDEST record goes and its card degrades to the chip; its blob is orphaned like an artifact's.
    private void DropOldestMcpApps()
    {
        var excess = _state.State.McpApps.Count - McpToolLimits.MaxAppsPerConversation;
        if (excess > 0)
        {
            _state.State.McpApps.RemoveRange(0, excess);
        }
    }

    /// <summary>
    /// Drops the records of every call that is no longer in the session. Run wherever the session lost turns: the
    /// retention prune and the edit or regenerate tail. Idempotent, and a no-op when there are no records.
    /// </summary>
    private void ReconcileMcpApps()
    {
        if (_state.State.McpApps.Count == 0)
        {
            return;
        }

        // An unreadable history proves nothing about which calls are live, so nothing is dropped (never fail destructively).
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var live = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet(StringComparer.Ordinal);
        _state.State.McpApps.RemoveAll(r => !live.Contains(r.CallId));
    }

    // The history projection's view of the records: a tool call that rendered an app replays with its view reference.
    private ChatTurn WithMcpApps(ChatTurn turn)
    {
        if (turn.ToolCalls is not { Count: > 0 } calls || _state.State.McpApps.Count == 0)
        {
            return turn;
        }

        var apps = _state.State.McpApps.ToDictionary(r => r.CallId, r => r.ToRef(), StringComparer.Ordinal);
        return turn with
        {
            ToolCalls = [.. calls.Select(c => apps.TryGetValue(c.ToolCallId, out var app) ? c with { McpApp = app } : c)],
        };
    }

    public async Task<string?> GetMcpAppResultAsync(string callId)
    {
        var record = _state.State.McpApps.Find(r => r.CallId == callId);
        if (record is null)
        {
            return null;
        }

        var stored = await _attachments.OpenReadAsync(this.GetPrimaryKeyString(), record.AttachmentId);
        return stored is null ? null : Encoding.UTF8.GetString(stored.Bytes);
    }

    // A fixed-shape name from a hash: a call id is chosen by the provider and may carry characters a blob name should not.
    private static string McpAppBlobName(string callId)
        => $"app-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callId)))[..24].ToLowerInvariant()}.bin";
}
