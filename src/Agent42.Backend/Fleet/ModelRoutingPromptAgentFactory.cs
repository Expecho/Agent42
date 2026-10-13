using Microsoft.Agents.AI;
using Microsoft.Agents.ObjectModel;

namespace Agent42.Backend;

/// <summary>
/// Creates a fleet agent on the chat client of the model its YAML names, or of the task's model when it names none
/// (ADR 0097). The keys are the user's tool-capable catalog ids (<c>provider:model</c>). An unknown model fails the
/// agent, so the planner gets the error and fixes the definition.
/// </summary>
public sealed class ModelRoutingPromptAgentFactory(IReadOnlyDictionary<string, PromptAgentFactory> factories, string defaultModel) : PromptAgentFactory
{
    private readonly Dictionary<string, PromptAgentFactory> _factories = new(factories, StringComparer.OrdinalIgnoreCase);

    public override Task<AIAgent?> TryCreateAsync(GptComponentMetadata promptAgent, CancellationToken cancellationToken = default)
    {
        var modelId = promptAgent.Model?.ModelNameHint;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = defaultModel;
        }

        return _factories.TryGetValue(modelId, out var factory)
            ? factory.TryCreateAsync(promptAgent, cancellationToken)
            : throw new InvalidOperationException($"Unknown model '{modelId}', use one of: {string.Join(", ", _factories.Keys)}.");
    }
}
