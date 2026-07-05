using System.Collections.Concurrent;
using Agent42.Agents;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Builds OTel-instrumented <see cref="IChatClient"/>s against the configured Azure AI
/// Foundry endpoint, one per model deployment. Clients are cached, so switching a
/// conversation between models doesn't rebuild the underlying HTTP pipeline each turn.
/// </summary>
public sealed class AzureOpenAIChatClientFactory : IChatClientFactory
{
    private readonly AzureOpenAIClient _client;
    private readonly string _otelSourceName;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

    public AzureOpenAIChatClientFactory(
        Uri endpoint, TokenCredential credential, string defaultDeployment, string otelSourceName)
    {
        _client = new AzureOpenAIClient(endpoint, credential);
        DefaultDeployment = defaultDeployment;
        _otelSourceName = otelSourceName;
    }

    public string DefaultDeployment { get; }

    public IChatClient GetClient(string? deployment)
    {
        var name = string.IsNullOrWhiteSpace(deployment) ? DefaultDeployment : deployment;
        return _clients.GetOrAdd(name, Build);
    }

    private IChatClient Build(string deployment) =>
        _client.GetChatClient(deployment)
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: _otelSourceName, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
}
