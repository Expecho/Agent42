using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The copilot agent kind (<see cref="AgentKinds.Copilot"/>): a chat whose turns run on the GitHub Copilot SDK's
/// agent loop instead of a <see cref="ChatClientAgent"/>. The loop lives behind <see cref="ICopilotAgentFactory"/>,
/// so this file never references the SDK.
///
/// <para><b>Same tools, no context providers.</b> Copilot gets the tools the ordinary composition would offer
/// (<see cref="SelectableTools"/>, after selection and MCP gating) as custom tools, always the SearXNG shims rather than
/// hosted ones, and the selected skills (<see cref="SelectedSkills"/>) as native skill folders. It gets no RAG,
/// long-term memory or images in phase 1. Those are the providers and the vision path a non-<see cref="ChatClientAgent"/>
/// cannot take, so documents are refused, <c>read_web_document</c> is not offered, and images are withheld.</para>
///
/// <para><b>Two switches, assigned with the agent by every <see cref="BuildAgent"/></b> like <c>_researchTodos</c>, so they always
/// describe the agent in use: <see cref="_copilotChat"/> (this chat runs the Copilot loop) and
/// <see cref="_copilotUnavailable"/> (it should, but cannot get one). Neither is turn-scoped.</para>
/// </summary>
public sealed partial class AgentGrain
{
    private const string CopilotIncognitoRefusal =
        "_Incognito isn't available in a copilot agent's chat: the agent keeps its own record of the conversation "
        + "while it works, so an incognito turn could not stay off the record. Turn incognito off to continue._\n\n";

    private const string CopilotNotConfigured =
        "_Copilot agents aren't available on this server. This chat can't continue._\n\n";

    /// <summary>True when the current <see cref="_agent"/> is the Copilot composition.</summary>
    private bool _copilotChat;

    /// <summary>
    /// Why the Copilot composition could not be built for the current <see cref="_agent"/>, shown to the user in
    /// place of a reply, or null. The grain refuses such a turn before it reaches the stand-in agent.
    /// </summary>
    private string? _copilotUnavailable;

    /// <summary>
    /// The contexts of the Copilot runs in flight on this activation (ADR 0106), added and removed by identity like
    /// <see cref="_clientToolBrokers"/>. The Copilot SDK calls a custom tool on its RPC thread, where the run's
    /// <see cref="TurnContext"/> does not flow, so <see cref="SchedulerBoundAIFunction"/> resolves it here. Only
    /// when exactly ONE run is in flight: with two (a reminder firing in a gap of a streamed Copilot turn) nothing
    /// says which run a call belongs to, so the tool gets <see cref="TurnContext.Closed"/> and refuses.
    /// </summary>
    private readonly List<TurnContext> _copilotRuns = [];

    // Decides whether the agent being built is the Copilot composition. A no-tools model gets a plain chat: Copilot's
    // whole value is its tool loop, and the same gate drops every other tool.
    private bool IsCopilotComposition(ModelCapabilities capabilities)
        => AgentKinds.IsCopilot(_state.State.AgentKind) && capabilities.SupportsTools;

    // Builds the Copilot composition: the factory's agent wrapped in the history mirror. A missing factory or a
    // refused request yields the session-preserving stand-in instead of failing the activation, because history,
    // binding and every other non-inference path must keep working.
    private AgentComposition BuildCopilotAgent(string modelId, ModelCapabilities capabilities, HashSet<string> enabled)
    {
        var name = _state.State.AgentName ?? "Agent42";
        var skills = SelectedSkills(capabilities, enabled);
        if (ServiceProvider.GetService<ICopilotAgentFactory>() is not { } factory)
        {
            return Unavailable(CopilotNotConfigured);
        }

        try
        {
            var inner = factory.CreateAgent(new CopilotAgentRequest(
                _userId,
                ConversationIdFromGrainKey(this.GetPrimaryKeyString()),
                _chatClientFactory,
                modelId,
                name,
                // No document awareness: a copilot chat has no documents (see the class remarks).
                WithRenderingHints(_state.State.SystemPrompt ?? Instructions),
                CopilotFunctions(capabilities, enabled, skills),
                [.. skills.Select(s => s.Path)]));
            return AgentComposition.Copilot(new CopilotChatAgent(inner, name), unavailable: null);
        }
        catch (CopilotUnavailableException ex)
        {
            return Unavailable(ex.Message);
        }

        AgentComposition Unavailable(string reason)
        {
            _logger.LogWarning(
                "Agent grain {GrainKey} cannot build its copilot agent on model {Model}: {Reason}",
                this.GetPrimaryKeyString(), modelId, reason);
            return AgentComposition.Copilot(new CopilotChatAgent(new CopilotUnavailableAgent(reason), name), reason);
        }
    }

    // The tools a Copilot chat never gets, whatever its selection (ADR 0089): the client tools (since ADR 0093
    // including get_current_location, so a Copilot chat has no location) and
    // update_shared_state wait on, or route through, the grain's own run (an async-local broker, the per-run
    // snapshot), which Copilot's RPC thread is not part of, and a 120 s wait under its RPC timeout is unmeasured.
    private static bool IsExcludedFromCopilot(string toolName)
        => ClientToolLimits.ToolNames.Contains(toolName) || toolName == SharedStateLimits.UpdateToolName;

    // The custom tools handed to Copilot. Only AIFunctions, so no hosted tool can arrive (and SelectableTools is asked
    // for the shims anyway). read_web_document is dropped because it feeds the RAG provider a copilot chat does not
    // have, read_document because a copilot chat refuses document upload and so has nothing to read (ADR 0098), and the ADR 0089 tools by IsExcludedFromCopilot. read_skill_resource is added when a skill is selected
    // (Copilot has no file tool to read one with). A
    // name the runtime would reject is skipped: one such name fails session creation, which would fail every turn.
    // Each tool is bound to the activation's scheduler, because Copilot calls tools from its own RPC thread.
    private List<AIFunction> CopilotFunctions(
        ModelCapabilities capabilities, HashSet<string> enabled, List<SkillInfo> skills)
    {
        var candidates = SelectableTools(capabilities, enabled, hostedTools: false)
            .OfType<AIFunction>()
            .Where(f => f.Name is not (ToolCatalog.ReadWebDocument or ToolCatalog.ReadDocument)
                && !IsExcludedFromCopilot(f.Name))
            .ToList();
        if (skills.Count > 0)
        {
            candidates.Add(ReadSkillResourceTool.Create(skills));
        }

        var scheduler = TaskScheduler.Current;
        var functions = new List<AIFunction>();
        foreach (var function in candidates)
        {
            if (!CopilotLimits.IsValidToolName(function.Name))
            {
                _logger.LogWarning(
                    "Agent grain {GrainKey} skipped tool '{Tool}' for its copilot agent: the runtime rejects the name",
                    this.GetPrimaryKeyString(), function.Name);
                continue;
            }

            functions.Add(new SchedulerBoundAIFunction(function, scheduler, () => _copilotRuns));
        }

        return functions;
    }

    // The text to send instead of running a copilot turn, or null to run it. Checked before the turn touches
    // anything: an incognito turn would have Copilot write its transcript to SessionFs during the turn, which the
    // grain cannot undo (the "memory provider is built at ACTIVATION" trap, ADR 0034).
    private string? CopilotTurnRefusal(TurnContext turn)
        => _copilotUnavailable ?? (_copilotChat && turn.Ephemeral ? CopilotIncognitoRefusal : null);

    // Registers a run of the Copilot composition, or does nothing (null) for any other agent. Read at run start: a
    // rebuild during the run cannot change which list entry the run removes.
    private TurnContext? OpenCopilotRun(TurnContext turn)
    {
        if (!_copilotChat)
        {
            return null;
        }

        _copilotRuns.Add(turn);
        return turn;
    }

    private void CloseCopilotRun(TurnContext? turn)
    {
        if (turn is not null)
        {
            _copilotRuns.Remove(turn);
        }
    }


    // Whether this turn's images may reach the model: never in a copilot chat (phase 1 has no image path through the
    // Copilot loop), otherwise the model's vision capability (ADR 0013).
    private bool CanSeeImages()
        => !_copilotChat && _chatClientFactory.GetCapabilities(_modelId).SupportsVision;

    // Deletes Copilot's transcript of this conversation (its SessionFs files) and its workspace directory (ADR 0107).
    // Keyed by the KIND, not by _copilotChat: a copilot chat that is currently on a no-tools model may still have a
    // transcript from earlier.
    // Best-effort, like the rest of PurgeAsync: a failure is logged and the purge carries on.
    private async Task DeleteCopilotTranscriptAsync()
    {
        if (!AgentKinds.IsCopilot(_state.State.AgentKind)
            || ServiceProvider.GetService<ICopilotAgentFactory>() is not { } factory)
        {
            return;
        }

        try
        {
            await factory.DeleteConversationAsync(_userId, ConversationIdFromGrainKey(this.GetPrimaryKeyString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Agent grain {GrainKey} could not delete its copilot transcript", this.GetPrimaryKeyString());
        }
    }
}
