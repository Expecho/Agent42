using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Builds the agent behind a copilot-kind chat (<see cref="AgentKinds.Copilot"/>): the GitHub Copilot SDK's agent
/// loop in production, an in-process twin in tests and DevHost. The grain never references the Copilot package; it
/// sees only this seam and an <see cref="AIAgent"/>.
///
/// <para>Resolved OPTIONALLY by the grain (<c>IServiceProvider.GetService</c>, the <c>/api/speech</c> pattern): a
/// silo host that registers none still activates every grain, and only a copilot turn is refused, with a clear
/// message. Every host registers one anyway (rule 7).</para>
/// </summary>
public interface ICopilotAgentFactory
{
    /// <summary>
    /// Builds the agent for one conversation. Must not do I/O. Throws <see cref="CopilotUnavailableException"/>
    /// when the request cannot be served, such as a provider kind the runtime cannot reach, and the grain then refuses the
    /// turn instead of failing the activation.
    /// </summary>
    AIAgent CreateAgent(CopilotAgentRequest request);

    /// <summary>
    /// Deletes everything the runtime stored for one conversation (its SessionFs files). Best-effort and
    /// idempotent: a conversation that never ran a copilot turn has nothing to delete.
    /// </summary>
    Task DeleteConversationAsync(string userId, string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a copilot agent is built from. <see cref="ChatClientFactory"/> is the grain's own per-user factory, resolved at
/// activation, so the agent runs on the same provider overlay the rest of the chat does and never re-resolves it.
/// <see cref="Tools"/> already passed selection, MCP gating and the runtime's name rule, and is marshalled onto the
/// grain. <see cref="SkillDirectories"/> are the selected skills' folders (built-in and uploaded); empty means skills
/// are off.
/// </summary>
public sealed record CopilotAgentRequest(
    string UserId,
    string ConversationId,
    IChatClientFactory ChatClientFactory,
    string ModelId,
    string Name,
    string Instructions,
    IReadOnlyList<AIFunction> Tools,
    IReadOnlyList<string> SkillDirectories);

/// <summary>
/// The copilot agent cannot be built for this request. The message is shown to the user as-is, so it must not carry
/// a key, an endpoint or anything else from the provider's configuration.
/// </summary>
public sealed class CopilotUnavailableException(string message) : Exception(message);
