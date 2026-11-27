using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The grain-side half of <c>read_web_document</c> (ADR 0066): the deferred agent rebuild. The tool itself is
/// <see cref="WebDocumentTool"/>; this half stays because it swaps <c>_agent</c>/<c>_session</c>, which only the
/// grain may do (ADR 0106, plan cc-12).
/// </summary>
public sealed partial class AgentGrain
{
    // Consumes the run's deferred-rebuild flag (TurnContext.WebDocumentIngested, ADR 0106) set by a mid-run ingest, rebuilding the agent so the newly
    // ingested document reaches the instructions AND attaches the always-on retrieval provider (ADR 0008)
    // from the next model call onward. Snapshot → rebuild → restore, the same shape as
    // RegisterDocumentAsync and BindAgentAsync; the difference is only WHEN it is safe to run.
    //
    // ⚠ THERE ARE TWO CONSUME SITES AND MISSING THE SECOND IS A SILENT BUG. ChatCoreAsync covers the live
    // chat path and /api/agent-run (which drains it). ReceiveReminder does NOT drain ChatCoreAsync — it
    // calls _agent.RunAsync directly and does its own serialize/write/index — so without a call there, a
    // reminder turn that ingested would persist the name list and lose the rebuild, leaving retrieval
    // detached for the rest of the activation while the instructions claim the document exists.
    // ReadWebDocument_AttachesRetrievalAfterReminderTurn is the half that catches it.
    //
    // A plain method call, so it adds NOTHING to either caller's cognitive complexity (S3776 counts
    // branching, not calls) — the branch stays here.
    private async Task RebuildForIngestedWebDocumentAsync(TurnContext turn)
    {
        if (!turn.TakeWebDocumentIngested())
        {
            return;
        }

        await RebuildAgentPreservingSessionAsync(_modelId);

        _logger.LogInformation(
            "Agent grain {GrainKey} rebuilt its agent after ingesting a web document ({Count} total)",
            this.GetPrimaryKeyString(), _webDocuments.Count);
    }
}
