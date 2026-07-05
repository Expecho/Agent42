using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Resolves an <see cref="IChatClient"/> for a specific model deployment. The agent
/// grain uses this so a single conversation can switch between the models deployed in
/// the configured Azure AI Foundry environment, rather than being pinned to one client.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>The deployment used when a conversation hasn't picked one yet.</summary>
    string DefaultDeployment { get; }

    /// <summary>
    /// Returns a chat client bound to <paramref name="deployment"/>, or the default
    /// deployment when it is null or blank.
    /// </summary>
    IChatClient GetClient(string? deployment);
}

/// <summary>
/// An <see cref="IChatClientFactory"/> that always hands back the same client regardless
/// of the requested deployment. Used by local dev (DevHost) and tests, where a single
/// canned/fake client stands in for Azure OpenAI.
/// </summary>
public sealed class SingleChatClientFactory(IChatClient client, string defaultDeployment = "default")
    : IChatClientFactory
{
    public string DefaultDeployment { get; } = defaultDeployment;

    public IChatClient GetClient(string? deployment) => client;
}
