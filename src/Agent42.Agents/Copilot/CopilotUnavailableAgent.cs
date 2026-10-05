using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Stands in for the Copilot agent when a copilot-kind chat cannot get one. That happens when the host registers no
/// <see cref="ICopilotAgentFactory"/> or the factory refused the request (<see cref="CopilotUnavailableException"/>),
/// for example after the user removed the provider the agent's model lives on.
///
/// <para>The grain refuses a turn before it reaches this agent, so running it is a bug and throws. What it MUST do
/// is keep the session intact. The serialized session holds the pointer to Copilot's transcript
/// (<c>sessionId</c>) and the history mirror (<c>stateBag</c>). An agent that dropped either one on its next
/// serialize would orphan the transcript, or empty the chat, over a transient outage. So it reads and writes the
/// same <c>{"sessionId":...,"stateBag":{...}}</c> shape the Copilot agent does.</para>
/// </summary>
public sealed class CopilotUnavailableAgent(string reason) : AIAgent
{
    /// <summary>The user-facing reason a turn is refused.</summary>
    public string Reason { get; } = reason;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new PreservedSession(null, new AgentSessionStateBag()));

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var preserved = (PreservedSession)session;
        var json = new JsonObject();
        if (preserved.SessionId is { } id)
        {
            json["sessionId"] = id;
        }

        json["stateBag"] = JsonNode.Parse(preserved.StateBag.Serialize().GetRawText());
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(json));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var id = serializedState.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        var bag = serializedState.TryGetProperty("stateBag", out var b) && b.ValueKind == JsonValueKind.Object
            ? AgentSessionStateBag.Deserialize(b)
            : new AgentSessionStateBag();
        return ValueTask.FromResult<AgentSession>(new PreservedSession(id, bag));
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new CopilotUnavailableException(Reason);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new CopilotUnavailableException(Reason);
#pragma warning disable CS0162 // An iterator needs a yield to be one; this one only throws.
        yield break;
#pragma warning restore CS0162
    }

    private sealed class PreservedSession(string? sessionId, AgentSessionStateBag stateBag) : AgentSession(stateBag)
    {
        public string? SessionId { get; } = sessionId;
    }
}
