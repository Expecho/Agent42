using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.ChatRouteState")]
public sealed class ChatRouteState
{
    [Id(0)]
    public string? ConversationId { get; set; }

    [Id(1)]
    public int TurnCount { get; set; }

    [Id(2)]
    public string? ModelOverride { get; set; }

    [Id(3)]
    public string? TargetAgentOverride { get; set; }
}

/// <summary>One chat route's current conversation (ADR 0111); see <see cref="IChatRouteGrain"/>.</summary>
public sealed class ChatRouteGrain : Grain, IChatRouteGrain
{
    private readonly IPersistentState<ChatRouteState> _state;

    public ChatRouteGrain(
        [PersistentState("chat-route", AgentGrain.StorageProviderName)] IPersistentState<ChatRouteState> state)
    {
        _state = state;
    }

    public Task<ChatRouteSnapshot> GetAsync()
        => Task.FromResult(new ChatRouteSnapshot(
            _state.State.ConversationId, _state.State.TurnCount, _state.State.ModelOverride, _state.State.TargetAgentOverride));

    public async Task<ChatRouteTurn> BeginTurnAsync(bool forceNew)
    {
        var state = _state.State;
        var rolledOver = !forceNew && state.ConversationId is not null
            && state.TurnCount >= ChatGatewayLimits.MaxTurnsPerConversation;
        var minted = forceNew || rolledOver || state.ConversationId is null;

        if (minted)
        {
            state.ConversationId = ChatGatewayLimits.NewConversationId();
            state.TurnCount = 0;
        }

        state.TurnCount++;
        await _state.WriteStateAsync();
        return new ChatRouteTurn(state.ConversationId!, minted, rolledOver, state.ModelOverride, state.TargetAgentOverride);
    }

    public async Task StartNewAsync()
    {
        _state.State.ConversationId = null;
        _state.State.TurnCount = 0;
        await _state.WriteStateAsync();
    }

    public async Task SetModelAsync(string? model)
    {
        _state.State.ModelOverride = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        await _state.WriteStateAsync();
    }

    public async Task SetAgentAsync(string? agentId)
    {
        _state.State.TargetAgentOverride = agentId;
        _state.State.ConversationId = null;
        _state.State.TurnCount = 0;
        await _state.WriteStateAsync();
    }
}
