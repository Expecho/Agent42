using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The replay half of MCP Apps (ADR 0133). The live card is fed from the stream; to draw it again after a reload the
/// raw result is saved to a blob and a small record names it (<see cref="AgentGrainState.McpApps"/>), through this class (plan cc-12 / ADR 0150).
///
/// <para>The lifecycle is its own design, not the artifact precedent: the list is keyed by tool call id and every
/// way a call can leave the conversation must drop its record. <see cref="StoreMcpAppsAsync"/> appends (and applies the
/// cap), <see cref="ReconcileMcpApps"/> drops the records whose call is no longer in the session (a retention prune, an
/// edit or regenerate), purge clears the list, and a fork copies none (<c>ConversationFork</c> has no apps field, so
/// the fork's records start empty and its cards show the plain chip). Blobs are orphaned, never deleted: the attachment
/// store has no delete, the same gap attachments and artifacts have.</para>
///
/// <para>Built per grain activation with <c>new</c> (ADR 0147); public only because this repo has no
/// <c>InternalsVisibleTo</c>. It holds no grain state: <paramref name="records"/> returns the live list on every call
/// (<c>ClearStateAsync</c> replaces it, ADR 0148) and <paramref name="liveCallIds"/> the call ids still in the session,
/// or null when the history is unreadable.</para>
/// </summary>
public sealed class McpAppStore(
    Func<List<McpAppRecord>> records,
    Func<IReadOnlySet<string>?> liveCallIds,
    IAttachmentStore attachments,
    ILogger logger,
    string grainKey)
{
    /// <summary>
    /// Saves this turn's app results to blob and records them, skipping an incognito turn (ADR 0034: the card renders
    /// live only). Called at the end-of-turn persist site BEFORE the write, so a crash in between orphans a blob rather
    /// than leaving a record that points at nothing. Best-effort per result: a failed save means that card degrades to
    /// the chip, never a failed turn.
    /// </summary>
    public async Task StoreAsync(TurnContext turn)
    {
        if (turn.Ephemeral)
        {
            return;
        }

        foreach (var (callId, result) in turn.AppResults)
        {
            await StoreOneAsync(result, callId);
        }
    }

    private async Task StoreOneAsync(McpAppResult result, string callId)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(result.ResultJson);
            var saved = await attachments.SaveAsync(
                grainKey, McpAppBlobName(callId), "application/octet-stream", bytes);
            var list = records();
            list.RemoveAll(r => r.CallId == callId);
            list.Add(new McpAppRecord(
                callId, result.App.ServerId, result.App.ToolName, result.App.ResourceUri, saved.Id, bytes.Length));
            DropOldest(list);
        }
        catch (Exception ex)
        {
            // Never the result: it is whatever a third-party server returned (ADR 0010).
            logger.LogWarning(ex, "Agent grain {GrainKey} could not store an MCP App result; its card will not replay",
                grainKey);
        }
    }

    // Past the cap the OLDEST record goes and its card degrades to the chip; its blob is orphaned like an artifact's.
    private static void DropOldest(List<McpAppRecord> list)
    {
        var excess = list.Count - McpToolLimits.MaxAppsPerConversation;
        if (excess > 0)
        {
            list.RemoveRange(0, excess);
        }
    }

    /// <summary>
    /// Drops the records of every call that is no longer in the session. Run wherever the session lost turns: the
    /// retention prune and the edit or regenerate tail. Idempotent, and a no-op when there are no records.
    /// </summary>
    public void Reconcile()
    {
        var list = records();
        if (list.Count == 0)
        {
            return;
        }

        // An unreadable history proves nothing about which calls are live, so nothing is dropped (never fail destructively).
        if (liveCallIds() is not { } live)
        {
            return;
        }

        list.RemoveAll(r => !live.Contains(r.CallId));
    }

    // The history projection's view of the records: a tool call that rendered an app replays with its view reference.
    public ChatTurn WithApps(ChatTurn turn)
    {
        var list = records();
        if (turn.ToolCalls is not { Count: > 0 } calls || list.Count == 0)
        {
            return turn;
        }

        var apps = list.ToDictionary(r => r.CallId, r => r.ToRef(), StringComparer.Ordinal);
        return turn with
        {
            ToolCalls = [.. calls.Select(c => apps.TryGetValue(c.ToolCallId, out var app) ? c with { McpApp = app } : c)],
        };
    }

    /// <summary>The saved raw result of one app call, or null when there is no record or its blob is gone.</summary>
    public async Task<string?> GetResultAsync(string callId)
    {
        var record = records().Find(r => r.CallId == callId);
        if (record is null)
        {
            return null;
        }

        var stored = await attachments.OpenReadAsync(grainKey, record.AttachmentId);
        return stored is null ? null : Encoding.UTF8.GetString(stored.Bytes);
    }

    // A fixed-shape name from a hash: a call id is chosen by the provider and may carry characters a blob name should not.
    private static string McpAppBlobName(string callId)
        => $"app-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callId)))[..24].ToLowerInvariant()}.bin";
}
