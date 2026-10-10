using System.Text;
using Agent42.Agents;
using Microsoft.Extensions.Logging;

namespace Agent42.Backend;

/// <summary>
/// Runs one turn of an exposed agent on behalf of an external MCP caller (ADR 0070) and hands the reply
/// text back — the half of the surface that is not protocol plumbing.
///
/// <para><b>It drains <c>ChatAsync</c>, not <c>EnqueuePromptAsync</c>.</b> The background path discards
/// the stream, so it has no reply to return. Draining and concatenating <c>TextDelta</c> is what
/// <c>/api/chat/{id}</c> already does, and it is deterministic — unlike relying on grain
/// non-reentrancy to order a following <c>GetHistoryAsync</c> behind an enqueued turn.</para>
/// </summary>
public static class AgentMcpRunner
{
    /// <summary>
    /// Authenticates nothing — <see cref="AgentMcpKeyGate"/> already did — and runs the turn.
    /// Returns the reply and the conversation id the caller can pass back to continue.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait for the reply. A seam, not a feature: without it no test can reach the
    /// timeout branch, and that branch is the one carrying the property this design exists for — that a
    /// turn the caller stopped waiting for is still persisted. It defaults to
    /// <see cref="AgentMcpLimits.RunTimeout"/>, so the tool surface passes nothing and there is no
    /// second number to drift.
    /// </param>
    public static async Task<AgentMcpResult> RunAsync(
        AgentMcpCaller caller, string prompt, string? conversationId, IGrainFactory grains,
        TimeSpan? timeout = null, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return AgentMcpResult.Failed("Provide a prompt.");
        }

        if (prompt.Length > AgentMcpLimits.MaxPromptChars)
        {
            // Refused, not truncated: this text lands verbatim in the OWNER's 2 MB session (ADR 0004) and
            // is re-sent to the model on every later turn of a reused conversation. Silently dropping the
            // tail would also hide the loss from a caller who could simply have sent less.
            return AgentMcpResult.Failed(
                $"Prompt exceeds the {AgentMcpLimits.MaxPromptChars} character limit.");
        }

        var (id, resolveError) = await ResolveConversationAsync(caller, conversationId, grains);
        if (resolveError is not null)
        {
            return AgentMcpResult.Failed(resolveError);
        }

        var agentGrain = grains.GetGrain<IAgentGrain>($"{caller.UserId}/{id}");

        // Listed for the owner, with the agent's identity, so a turn an external caller drove is findable
        // in the sidebar rather than invisible. Touch is idempotent, so a continued conversation just
        // bumps its position.
        await grains.GetGrain<IConversationIndexGrain>(caller.UserId)
            .TouchAsync(id, prompt, caller.Agent.Id, caller.Agent.Name);

        // Snapshot the persona (ADR 0003), with memory and push notifications removed from the tool
        // selection (see WithoutMemoryOrPush). Idempotent and immutable-once-bound, so on a continued
        // conversation this is a no-op and the binding checked above is the one that runs.
        await agentGrain.BindAgentAsync(new AgentBinding(
            caller.Agent.Id, caller.Agent.Name, caller.Agent.SystemPrompt, caller.Agent.Model,
            WithoutMemoryOrPush(caller.Agent.EnabledTools), caller.Agent.Kind));

        return await DrainWithinBudgetAsync(
            agentGrain, prompt, id, timeout ?? AgentMcpLimits.RunTimeout, logger);
    }

    /// <summary>
    /// Validates a caller-supplied conversation id or mints a fresh one, and confirms an existing
    /// conversation belongs to the calling agent.
    ///
    /// <para><b>Two defences, and neither alone is enough.</b> The shape check
    /// (<see cref="AgentMcpLimits.IsValidConversationId"/>) stops a traversal or an ADR 0016 continuous id
    /// from reaching the grain key. But the owner is resolved from the API key, so a <i>well-shaped</i> id
    /// still addresses one of that owner's other MCP conversations — and <c>BindAgentAsync</c> is
    /// immutable-once-bound, so a conversation already bound to agent B keeps B's persona, prompt, tools
    /// and history. A holder of agent A's key supplying that id would therefore run as B and read B's
    /// content back. Hence the binding comparison, which fails closed on a mismatch.</para>
    ///
    /// <para>The mismatch message is deliberately the same as the malformed one: a caller must not be able
    /// to enumerate which of the owner's conversations exist by the shape of the refusal.</para>
    /// </summary>
    private static async Task<(string Id, string? Error)> ResolveConversationAsync(
        AgentMcpCaller caller, string? conversationId, IGrainFactory grains)
    {
        const string Rejection = "Unknown conversationId.";

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return (AgentMcpLimits.NewConversationId(), null);
        }

        if (!AgentMcpLimits.IsValidConversationId(conversationId))
        {
            return ("", Rejection);
        }

        var binding = await grains
            .GetGrain<IAgentGrain>($"{caller.UserId}/{conversationId}")
            .GetAgentBindingAsync();

        // A never-used id is unbound, which is fine — it binds to this agent below. A bound one must match.
        return binding is null || string.Equals(binding.AgentId, caller.Agent.Id, StringComparison.Ordinal)
            ? (conversationId, null)
            : ("", Rejection);
    }

    /// <summary>
    /// Enumerates the turn <b>to completion</b> on a detached task and bounds only the caller's WAIT.
    ///
    /// <para><b>This is the part that is easy to get backwards.</b> The session write sits after the
    /// streaming loop, inside the iterator body — so a consumer that stops pulling leaves the iterator
    /// suspended at a <c>yield return</c> and the turn is <i>never persisted</i>: tokens spent, the
    /// conversation listed with a title and no messages, the search index never updated, and no
    /// client-side recovery that could repair it. ADR 0046's property is narrower than it looks — Kestrel
    /// discards <i>writes</i> while the endpoint keeps pulling, which is not the same as abandoning the
    /// enumerator. So the timeout races the wait, never the turn.</para>
    /// </summary>
    private static async Task<AgentMcpResult> DrainWithinBudgetAsync(
        IAgentGrain agentGrain, string prompt, string conversationId, TimeSpan timeout, ILogger? logger)
    {
        var drain = Task.Run(async () =>
        {
            var reply = new StringBuilder();
            // interactive: false — nothing is watching this stream, so the follow-up suggestion call
            // (ADR 0036) would be a second model call whose result is discarded (C13).
            await foreach (var evt in agentGrain.ChatAsync(
                prompt, model: null, attachments: null, ephemeral: false, interactive: false))
            {
                if (evt.Type == AgentStreamEventType.TextDelta && evt.Text is { Length: > 0 } text)
                {
                    reply.Append(text);
                }
                else if (evt.Type == AgentStreamEventType.TextReset)
                {
                    // A research loop's later iteration started its answer (ADR 0082): the text so far was a
                    // draft, and the caller gets what the owner's chat history will show — the final answer.
                    reply.Clear();
                }
            }

            return reply.ToString();
        });

        // The timer is cancelled on the success path. Left uncancelled it stays rooted for the FULL
        // timeout after every call that beat it — one live timer per call for two minutes, on a path with
        // no rate limiting (ADR 0070's recorded residual).
        using var timer = new CancellationTokenSource();
        var finished = await Task.WhenAny(drain, Task.Delay(timeout, timer.Token));
        if (finished != drain)
        {
            // Observe the fault so an eventual failure cannot surface as an unobserved task exception,
            // and let the turn run on to its persist.
            _ = drain.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            return new AgentMcpResult(
                conversationId,
                $"The agent did not finish within {timeout.TotalSeconds:0} seconds. It is still running, "
                + "and its reply will appear in the owner's chat history. Sending the same prompt again "
                + "with this conversationId will queue a SECOND turn behind it, not read the first.",
                IsError: true);
        }

        timer.Cancel();

        try
        {
            return new AgentMcpResult(conversationId, AgentMcpLimits.CapReply(await drain), IsError: false);
        }
        catch (Exception ex)
        {
            // A model or provider failure throws straight out of the enumerator — ChatCoreAsync has no
            // try/catch around RunStreamingAsync, which is why the browser path shows "Transmission
            // failed" (ADR 0046). Three things follow, and only the first is fixed here.
            //
            // The reason is CURATED, never the exception (the ConfigHealth.ProbeAsync discipline): an
            // unauthenticated-by-Entra caller must not receive a provider's message, endpoint or stack.
            // Letting it propagate would also hand the SDK an arbitrary exception to render, which is
            // behaviour this repo has never measured.
            //
            // What is NOT fixed, deliberately: the turn does not persist (the write sits after the
            // streaming loop), so TouchAsync has already listed a conversation that will stay empty. That
            // is the same orphan /api/agent-run leaves on a faulted background turn, and closing it is a
            // change to that older surface too. ADR 0070 records it rather than claiming the timeout
            // design covers it.
            logger?.LogError(ex, "MCP ask_agent turn failed for conversation {ConversationId}", conversationId);

            return new AgentMcpResult(
                conversationId, "The agent failed to produce a reply. Try again.", IsError: true);
        }
    }

    /// <summary>
    /// The agent's tool selection with <see cref="ToolCatalog.Memory"/> (ADR 0056/0057) and
    /// <see cref="ToolCatalog.PushNotification"/> (ADR 0075) removed.
    ///
    /// <para><b>Exclusion happens at BIND time, deliberately, and not through a turn-scoped grain
    /// field.</b> <c>_ephemeral</c>, <c>_interactiveTurn</c> and
    /// <c>_webDocumentIngested</c> are a family that lives for the <i>activation</i> and is cleared in
    /// three places; any new per-turn field inherits that trap. A binding is materialised into grain
    /// state once and read as a value on every rebuild, so it cannot leak into anyone else's turn.</para>
    ///
    /// <para><b>Filtered, never rebuilt.</b> A null selection is the <c>null == all</c> sentinel, so it
    /// expands to <see cref="ToolCatalog.AllSelectableToolKeys"/> — which carries the <c>skills</c> and
    /// <c>mcp</c> wildcards. Substituting a hand-written list would silently drop both.</para>
    ///
    /// <para><b>Why these two and nothing else.</b> A memory bank is the user's, spans every conversation,
    /// and has no delete path (ADR 0056). Retaining turns an external caller drove would let a key holder
    /// write permanently into the owner's memory, and recall would hand that caller content from the
    /// owner's unrelated conversations.</para>
    ///
    /// <para>Push notifications (ADR 0075) are excluded for a different reason, and one specific to THIS
    /// surface: this ADR chose deliberately that an <c>ask_agent</c> turn lands in the owner's sidebar
    /// <b>silently</b>, because the caller already has the reply in hand. A model-invoked notification
    /// would reverse that by default — the key is in <see cref="ToolCatalog.AllSelectableToolKeys"/>, so a
    /// <c>null == all</c> agent would have it — and would let a key holder put <b>model-authored text they
    /// can influence</b> on the owner's lock screen. Note the asymmetry is intentional:
    /// <c>/api/agent-run</c> keeps the tool, because that surface already notifies by design (the ADR 0028
    /// away-reply push) and notifying is a plausible reason to drive an agent from a cron job.</para>
    ///
    /// <para><b>It only ever NARROWS.</b> Both keys are dropped from whatever the owner selected; nothing
    /// is added, and an owner who never enabled them sees no change.</para>
    /// </summary>
    private static IReadOnlyList<string> WithoutMemoryOrPush(IReadOnlyList<string>? enabledTools)
        => (enabledTools ?? ToolCatalog.AllSelectableToolKeys)
            .Where(key => !string.Equals(key, ToolCatalog.Memory, StringComparison.Ordinal)
                && !string.Equals(key, ToolCatalog.PushNotification, StringComparison.Ordinal))
            .ToList();
}

/// <summary>
/// What one <c>ask_agent</c> call produced: the conversation id to continue with, and either the reply
/// or a refusal. The id rides back even on a timeout, because the turn is still recorded under it.
/// </summary>
public sealed record AgentMcpResult(string ConversationId, string Text, bool IsError)
{
    /// <summary>A refusal that never reached a conversation, so it carries no id.</summary>
    public static AgentMcpResult Failed(string message) => new("", message, IsError: true);
}
