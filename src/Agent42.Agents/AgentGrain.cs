using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// An Orleans grain wrapping a Microsoft Agent Framework <see cref="AIAgent"/>.
/// Streams replies and persists the conversation session in the "agent-state"
/// storage provider (Cosmos DB in production).
/// </summary>
public sealed class AgentGrain : Grain, IAgentGrain
{
    public const string StorageProviderName = "agent-state";

    private const string Instructions = """
        You are Agent 42, a friendly and helpful assistant.
        Answer the user's questions concisely and accurately.
        """;

    private readonly IPersistentState<AgentGrainState> _state;
    private readonly IChatClient _chatClient;
    private readonly ILogger<AgentGrain> _logger;

    private AIAgent _agent = null!;
    private AgentSession _session = null!;

    public AgentGrain(
        [PersistentState("agent", StorageProviderName)] IPersistentState<AgentGrainState> state,
        IChatClient chatClient,
        ILogger<AgentGrain> logger)
    {
        _state = state;
        _chatClient = chatClient;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _agent = new ChatClientAgent(_chatClient, instructions: Instructions, name: "Agent42");

        _session = _state.State.SerializedSession is { Length: > 0 } serialized
            ? await _agent.DeserializeSessionAsync(
                JsonSerializer.Deserialize<JsonElement>(serialized), cancellationToken: cancellationToken)
            : await _agent.CreateSessionAsync(cancellationToken);

        _logger.LogInformation("Agent grain {GrainKey} activated (existing session: {HasSession})",
            this.GetPrimaryKeyString(), _state.State.SerializedSession is not null);

        await base.OnActivateAsync(cancellationToken);
    }

    public async IAsyncEnumerable<string> ChatAsync(string message)
    {
        _logger.LogInformation("Agent grain {GrainKey} received a message ({Length} chars)",
            this.GetPrimaryKeyString(), message.Length);

        await foreach (AgentResponseUpdate update in _agent.RunStreamingAsync(message, _session))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        await _state.WriteStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} completed a turn and persisted its session",
            this.GetPrimaryKeyString());
    }

    public Task<IReadOnlyList<ChatTurn>> GetHistoryAsync()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return Task.FromResult<IReadOnlyList<ChatTurn>>([]);
        }

        var turns = messages
            .Where(m => (m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
                && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new ChatTurn(m.Role.Value, m.Text))
            .ToList();

        return Task.FromResult<IReadOnlyList<ChatTurn>>(turns);
    }
}
