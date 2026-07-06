using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.ConversationIndexState")]
public sealed class ConversationIndexState
{
    [Id(0)]
    public List<ConversationInfo> Conversations { get; set; } = [];
}

public sealed class ConversationIndexGrain : Grain, IConversationIndexGrain
{
    private const int MaxTitleLength = 60;

    private readonly IPersistentState<ConversationIndexState> _state;

    public ConversationIndexGrain(
        [PersistentState("conversations", AgentGrain.StorageProviderName)] IPersistentState<ConversationIndexState> state)
    {
        _state = state;
    }

    public async Task TouchAsync(string conversationId, string firstPrompt)
    {
        var conversations = _state.State.Conversations;
        var existing = conversations.FindIndex(c => c.Id == conversationId);

        if (existing >= 0)
        {
            conversations[existing] = conversations[existing] with { UpdatedAt = DateTimeOffset.UtcNow };
        }
        else
        {
            var title = firstPrompt.Trim();
            if (title.Length > MaxTitleLength)
            {
                title = title[..MaxTitleLength] + "…";
            }

            conversations.Add(new ConversationInfo(conversationId, title, DateTimeOffset.UtcNow));
        }

        await _state.WriteStateAsync();
    }

    public Task<IReadOnlyList<ConversationInfo>> GetConversationsAsync()
        => Task.FromResult<IReadOnlyList<ConversationInfo>>(
            [.. _state.State.Conversations.OrderByDescending(c => c.UpdatedAt)]);

    public async Task RemoveAsync(string conversationId)
    {
        if (_state.State.Conversations.RemoveAll(c => c.Id == conversationId) > 0)
        {
            await _state.WriteStateAsync();
        }
    }
}
