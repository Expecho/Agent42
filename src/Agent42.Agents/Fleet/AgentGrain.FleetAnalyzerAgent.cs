using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The composition of a <see cref="AgentKinds.FleetAnalyzer"/> conversation (ADR 0123): a plain
/// <see cref="ChatClientAgent"/> with exactly the eight analyzer tools and <c>get_date</c>. The kind decides the tools, so
/// <c>EnabledTools</c> and the user's default selection are not read, and there are no context providers (memory, skills,
/// document search), no hosted tools and no MCP. A model that takes no tools gets none of them.
/// </summary>
public sealed partial class AgentGrain
{
    private AgentComposition ComposeFleetAnalyzerAgent(string modelId, ModelCapabilities capabilities)
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(GetDate, name: "get_date", description: "Get the current date and time. Returns a string in the local timezone."),
            .. _fleetAnalyzerTools.Functions(),
        ];

        var agent = new ChatClientAgent(
            _chatClientFactory.GetClient(modelId),
            new ChatClientAgentOptions
            {
                Name = FleetAnalyzerDefaults.Name,
                ChatOptions = new ChatOptions
                {
                    Instructions = FleetAnalyzerDefaults.SystemPrompt,
                    Tools = capabilities.SupportsTools ? tools : [],
                    AdditionalProperties = new() { [PromptCacheKey.PropertyName] = PromptCacheKey.For(this.GetPrimaryKeyString()) },
                },
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    StorageInputRequestMessageFilter = ExcludeInjectedContextMessages,
                    StorageInputResponseMessageFilter = StripReasoningFromStorage,
                }),
            });
        return new AgentComposition(agent, ResearchTodos: null, CopilotChat: false, CopilotUnavailable: null);
    }
}
