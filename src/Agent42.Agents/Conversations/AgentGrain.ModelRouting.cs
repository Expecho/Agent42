using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    /// <summary>
    /// What <see cref="ResolveTurnModelAsync"/> hands back: the model to switch to (null keeps the current), the label event, and the
    /// image model for a one-off picture turn that switches nothing (ADR 0138).
    /// </summary>
    private readonly record struct TurnModel(string? Model, AgentStreamEvent? Routed, string? ImageModel = null);

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
        if (!string.IsNullOrEmpty(_state.State.AgentId))
        {
            return new TurnModel(null, null);
        }

        if (turn.Ephemeral || _runsInFlight > 1)
        {
            return UnroutedAuto();
        }

        if (await ResolveRouterAsync() is not { } router)
        {
            // A client that still sends `auto` after the router is gone: run on the current model and forget Auto.
            _state.State.AutoModel = false;
            return UnroutedAuto();
        }

        _state.State.AutoModel = true;

        var decision = await RouteSafelyAsync(router, message, attachments);

        // The router awaited: a run that started meanwhile now shares this activation, and a switch would swap the agent and
        // session under it (ADR 0106). Keep the current model, and an image decision is dropped with it.
        if (decision is null || _runsInFlight > 1)
        {
            return UnroutedAuto();
        }

        return await ApplyDecisionAsync(decision);
    }

    // The router call itself. A failure keeps the current model (ADR 0137), so it never throws out of the turn.
    private async Task<RouterDecision?> RouteSafelyAsync(IModelRouter router, string message, IReadOnlyList<AttachmentRef>? attachments)
    {
        try
        {
            // An image model is never the "current" model (null encodes "none", ADR 0138): a chat winner then switches the
            // conversation off it, exactly as before, and the sticky rule never keeps a picture endpoint for a text turn.
            var current = IsImageModel(_modelId) ? null : _modelId;
            return await router.RouteAsync(
                new RouterTurn(message, HasImages(attachments), ToolsEnabledForRouting(), current, PreviousReplyWasImage()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent grain {GrainKey} model router failed; keeping {Model}", this.GetPrimaryKeyString(), _modelId);
            return null;
        }
    }

    private async Task<TurnModel> ApplyDecisionAsync(RouterDecision decision)
    {
        if (decision.Changed)
        {
            await RefreshChatClientFactoryAsync();
        }

        _logger.LogInformation("Agent grain {GrainKey} routed the turn to {Model} (changed: {Changed}, image: {Image})",
            this.GetPrimaryKeyString(), decision.ModelId, decision.Changed, decision.ImageGeneration);
        var routed = new AgentStreamEvent(AgentStreamEventType.Routed, Text: decision.DisplayName, Model: decision.ModelId);

        // A picture is a one-off (ADR 0138): the conversation's model, agent and Auto flag stay as they were, and the label is
        // always sent, because it is what tells the user why they got a picture.
        if (decision.ImageGeneration)
        {
            return new TurnModel(null, routed, decision.ModelId);
        }

        return new TurnModel(decision.Changed ? decision.ModelId : null, routed);
    }

    // An Auto turn that was not routed. Normally it keeps the current model, but a conversation left on an image model must not
    // send a text turn to the picture endpoint, so it falls back to the user's default chat model (ADR 0138). A turn that
    // overlaps another run cannot switch (ADR 0106) and stays where it is.
    private TurnModel UnroutedAuto()
    {
        var fallback = _chatClientFactory.DefaultModelId;
        return _runsInFlight <= 1 && !string.IsNullOrEmpty(fallback) && IsImageModel(_modelId)
            ? new TurnModel(fallback, null)
            : new TurnModel(null, null);
    }

    private bool IsImageModel(string modelId) => _chatClientFactory.GetCapabilities(modelId).SupportsImageGeneration;

    // The image model this turn runs on, or null for a chat turn: the router's one-off pick, else the conversation's own model when
    // it is an image model. One helper so ChatTurnAsync keeps a single branch (S3776).
    private string? ImageModelForTurn(TurnModel routing)
        => routing.ImageModel ?? (IsImageModel(_modelId) ? _modelId : null);

    // True when the last session message is an assistant image turn. Read from the id KEY, not the ref: the key survives the
    // no-vision strip and fork, and the last message is never pruned by retention (ADR 0138).
    private bool PreviousReplyWasImage()
        => LastSessionMessage() is { } last && last.Role == ChatRole.Assistant && AssistantImageId(last) is not null;

    private ChatMessage? LastSessionMessage()
        => _session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) && messages is { Count: > 0 } ? messages[^1] : null;

    // The chat-client factory is resolved once per activation (ADR 0018), so a model the user added after this grain woke is unknown
    // to it, and a switch onto that model would fall back to the default model while the label named the new one. The router reads
    // the catalog fresh every turn, so reload the factory before switching onto what it chose. A version-stamped cache hit when
    // nothing changed, so it costs one grain read. A failed reload keeps the old factory, as before.
    private async Task RefreshChatClientFactoryAsync()
    {
        try
        {
            _chatClientFactory = await _chatClientFactoryProvider.GetForUserAsync(_userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent grain {GrainKey} could not refresh its model providers before routing", this.GetPrimaryKeyString());
        }
    }

    // The vision filter must hold for the whole conversation: history images are re-sent every turn (gotchas: CanSeeImages).
    private bool HasImages(IReadOnlyList<AttachmentRef>? attachments)
        => (attachments?.Any(IsImage) ?? false) || _state.State.TurnAttachments.Any(turnAttachments => turnAttachments.Any(IsImage));

    private static bool IsImage(AttachmentRef a) => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    // Tools are on unless the conversation's set is an explicit empty list (null means all, ADR 0021). A no-tools model would
    // force a history strip on the switch, so it is not offered while they are on.
    private bool ToolsEnabledForRouting() => _state.State.EnabledTools is null || _state.State.EnabledTools.Count > 0;
}
