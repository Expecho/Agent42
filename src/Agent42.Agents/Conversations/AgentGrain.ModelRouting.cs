using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The Auto model (ADR 0137): the grain side of per-turn model routing. The router decides; this file only applies it
/// through the existing <c>SwitchModelAsync</c>, so the only state a routed turn changes is the ordinary model switch plus
/// the <see cref="AgentGrainState.AutoModel"/> flag. The decision itself is a local of the turn, never a grain field
/// (ADR 0106).
///
/// <para>The reserved wire value <c>auto</c> is never persisted: <c>DeploymentName</c> always holds a real model.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>What <see cref="ResolveTurnModelAsync"/> hands back: the model to switch to (null keeps the current) and the label event.</summary>
    private readonly record struct TurnModel(string? Model, AgentStreamEvent? Routed);

    // The picker restores to Auto only while the user still has a router; otherwise the real model (a stale Auto, ADR 0137).
    public async Task<string> GetSelectedModelAsync()
        => _state.State.AutoModel && await ResolveRouterAsync() is not null ? AutoModelId.Value : _modelId;

    // The router for this user, or null: no port registered, or the user has no enabled DecisionModel provider.
    private async Task<IModelRouter?> ResolveRouterAsync()
    {
        if (_modelRouterProvider is null)
        {
            return null;
        }

        try
        {
            return await _modelRouterProvider.GetForUserAsync(_userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent grain {GrainKey} could not resolve the model router", this.GetPrimaryKeyString());
            return null;
        }
    }

    private static void ThrowIfAuto(string? model)
    {
        if (AutoModelId.IsAuto(model))
        {
            throw new ArgumentException("'auto' is a chat-picker value, not a model; it cannot be bound or persisted.", nameof(model));
        }
    }

    // Turns the request's model value into the model this turn runs on. `auto` asks the router; any other non-blank value is an
    // explicit pick and clears the Auto flag; a blank one (a background run) touches nothing. Never returns `auto`.
    private async Task<TurnModel> ResolveTurnModelAsync(
        string? model, string message, IReadOnlyList<AttachmentRef>? attachments, TurnContext turn)
    {
        if (!AutoModelId.IsAuto(model))
        {
            // Cleared even on an incognito turn: the switch below still moves the model, so Auto would otherwise outlive it.
            if (!string.IsNullOrWhiteSpace(model))
            {
                _state.State.AutoModel = false;
            }

            return new TurnModel(model, null);
        }

        // Agent chats keep their pinned model, an incognito turn writes and sends nothing extra, and a turn that overlaps another
        // run would swap the agent and session under it (ADR 0106): none of them routes.
        if (!string.IsNullOrEmpty(_state.State.AgentId) || turn.Ephemeral || _runsInFlight > 1)
        {
            return new TurnModel(null, null);
        }

        if (await ResolveRouterAsync() is not { } router)
        {
            // A client that still sends `auto` after the router is gone: run on the current model and forget Auto.
            _state.State.AutoModel = false;
            return new TurnModel(null, null);
        }

        var wasAuto = _state.State.AutoModel;
        _state.State.AutoModel = true;

        RouterDecision? decision;
        try
        {
            decision = await router.RouteAsync(new RouterTurn(message, HasImages(attachments), ToolsEnabledForRouting(), _modelId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent grain {GrainKey} model router failed; keeping {Model}", this.GetPrimaryKeyString(), _modelId);
            decision = null;
        }

        // The router awaited: a run that started meanwhile now shares this activation, and a switch would swap the agent and
        // session under it (ADR 0106). Keep the current model.
        if (decision is null || _runsInFlight > 1)
        {
            return new TurnModel(null, null);
        }

        _logger.LogInformation("Agent grain {GrainKey} routed the turn to {Model} (changed: {Changed})",
            this.GetPrimaryKeyString(), decision.ModelId, decision.Changed);
        var label = decision.Changed || !wasAuto
            ? new AgentStreamEvent(AgentStreamEventType.Routed, Text: decision.DisplayName, Model: decision.ModelId)
            : null;
        return new TurnModel(decision.Changed ? decision.ModelId : null, label);
    }

    // The vision filter must hold for the whole conversation: history images are re-sent every turn (gotchas: CanSeeImages).
    private bool HasImages(IReadOnlyList<AttachmentRef>? attachments)
        => (attachments?.Any(IsImage) ?? false) || _state.State.TurnAttachments.Any(turnAttachments => turnAttachments.Any(IsImage));

    private static bool IsImage(AttachmentRef a) => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    // Tools are on unless the conversation's set is an explicit empty list (null means all, ADR 0021). A no-tools model would
    // force a history strip on the switch, so it is not offered while they are on.
    private bool ToolsEnabledForRouting() => _state.State.EnabledTools is null || _state.State.EnabledTools.Count > 0;
}
