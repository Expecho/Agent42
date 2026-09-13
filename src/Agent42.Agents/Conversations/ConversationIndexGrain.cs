using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.ConversationIndexState")]
public sealed class ConversationIndexState
{
    [Id(0)]
    public List<ConversationInfo> Conversations { get; set; } = [];

    // The user's custom conversation groups (ADR 0073). Trailing [Id(1)] on a property with an
    // initializer → no state migration: a document written before groups carries no "Groups" key and
    // binds to the empty list. Declared List<ChatGroup>, never IReadOnlyList — a collection expression
    // into IReadOnlyList compiles to a compiler-internal type whose name lands in the Cosmos document's
    // $type and cannot be constructed on read, stranding the whole state root (see gotchas).
    [Id(1)]
    public List<ChatGroup> Groups { get; set; } = [];
}

public sealed class ConversationIndexGrain : Grain, IConversationIndexGrain
{
    private const int MaxTitleLength = 60;

    // Bounds the state item against ADR 0004's 2 MB cap. Far past any human use (~10 KB serialized at
    // the limit), and the reason it exists at all is that nothing else stops a scripted client from
    // growing this list without end.
    private const int MaxGroups = 50;

    private readonly IPersistentState<ConversationIndexState> _state;

    public ConversationIndexGrain(
        [PersistentState("conversations", AgentGrain.StorageProviderName)] IPersistentState<ConversationIndexState> state)
    {
        _state = state;
    }

    public async Task TouchAsync(string conversationId, string firstPrompt, string? agentId = null, string? agentName = null)
    {
        var conversations = _state.State.Conversations;
        var existing = conversations.FindIndex(c => c.Id == conversationId);

        if (existing >= 0)
        {
            // Preserve the recorded agent identity — only the first (creating) touch sets it,
            // and later turns of an agent chat pass null. The `with` expression also carries Pinned
            // (ADR 0045) and GroupId (ADR 0073) forward for free, which is exactly why both have a
            // regression test: a later refactor to an explicit `new ConversationInfo(...)` would
            // silently drop the user's pin and their filing on the next turn of the conversation.
            conversations[existing] = conversations[existing] with { UpdatedAt = DateTimeOffset.UtcNow };
        }
        else
        {
            conversations.Add(new ConversationInfo(conversationId, Cap(firstPrompt), DateTimeOffset.UtcNow, agentId, agentName));
        }

        await _state.WriteStateAsync();
    }

    public async Task SetPinnedAsync(string conversationId, bool pinned)
    {
        var conversations = _state.State.Conversations;
        var existing = conversations.FindIndex(c => c.Id == conversationId);

        // No-op for an unlisted conversation, and for a value that is already set —
        // only a real change earns a write (mirrors RemoveAsync).
        if (existing < 0 || conversations[existing].Pinned == pinned)
        {
            return;
        }

        conversations[existing] = conversations[existing] with { Pinned = pinned };
        await _state.WriteStateAsync();
    }

    public Task<IReadOnlyList<ConversationInfo>> GetConversationsAsync()
        => Task.FromResult<IReadOnlyList<ConversationInfo>>(
            [.. _state.State.Conversations
                .OrderByDescending(c => c.Pinned)
                .ThenByDescending(c => c.UpdatedAt)]);

    public async Task RemoveAsync(string conversationId)
    {
        if (_state.State.Conversations.RemoveAll(c => c.Id == conversationId) > 0)
        {
            await _state.WriteStateAsync();
        }
    }

    /* ————— custom conversation groups (ADR 0073) ————— */

    // A collection expression is fine here: this is a RETURN value, not persisted state, exactly as
    // GetConversationsAsync above has shipped since ADR 0045.
    public Task<IReadOnlyList<ChatGroup>> GetGroupsAsync()
        => Task.FromResult<IReadOnlyList<ChatGroup>>([.. _state.State.Groups.OrderBy(g => g.Order)]);

    public async Task<ChatGroup?> CreateGroupAsync(string name)
    {
        if (_state.State.Groups.Count >= MaxGroups)
        {
            return null;
        }

        // Returned rather than void: the caller has no other way to learn the minted id, and the
        // client needs it immediately to file a conversation into the group it just created.
        var group = new ChatGroup(Guid.NewGuid().ToString("n"), Cap(name), _state.State.Groups.Count);
        _state.State.Groups.Add(group);
        await _state.WriteStateAsync();
        return group;
    }

    public async Task RenameGroupAsync(string groupId, string name)
    {
        var groups = _state.State.Groups;
        var existing = groups.FindIndex(g => g.Id == groupId);
        var capped = Cap(name);

        // Unknown group, or a name that is already what was asked for: no write (SetPinnedAsync's rule).
        if (existing < 0 || groups[existing].Name == capped)
        {
            return;
        }

        groups[existing] = groups[existing] with { Name = capped };
        await _state.WriteStateAsync();
    }

    public async Task DeleteGroupAsync(string groupId)
    {
        if (_state.State.Groups.RemoveAll(g => g.Id == groupId) == 0)
        {
            return;
        }

        // Ungroup the members in the SAME write. Deleting a group never deletes a conversation — the
        // chats fall back into the ordinary recency walk.
        var conversations = _state.State.Conversations;
        for (var i = 0; i < conversations.Count; i++)
        {
            if (conversations[i].GroupId == groupId)
            {
                conversations[i] = conversations[i] with { GroupId = null };
            }
        }

        Renumber();
        await _state.WriteStateAsync();
    }

    public async Task ReorderGroupsAsync(IReadOnlyList<string> groupIds)
    {
        var groups = _state.State.Groups;
        var named = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ChatGroup>(groups.Count);

        foreach (var id in groupIds)
        {
            var match = groups.Find(g => g.Id == id);
            // Ids naming no group are ignored; the HashSet also stops a repeated id duplicating a group.
            if (match is not null && named.Add(id))
            {
                ordered.Add(match);
            }
        }

        // Anything the caller omitted keeps its relative order AFTER the named ones, so a client
        // working from a stale list can never drop a group out of the sidebar.
        ordered.AddRange(groups.Where(g => !named.Contains(g.Id)));

        if (ordered.Select(g => g.Id).SequenceEqual(groups.Select(g => g.Id), StringComparer.Ordinal))
        {
            return;
        }

        _state.State.Groups = ordered;
        Renumber();
        await _state.WriteStateAsync();
    }

    public async Task SetGroupAsync(string conversationId, string? groupId)
    {
        var conversations = _state.State.Conversations;
        var existing = conversations.FindIndex(c => c.Id == conversationId);

        if (existing < 0 || conversations[existing].GroupId == groupId)
        {
            return;
        }

        // A non-null id that names no group is a NO-OP, deliberately not a silent clear: clearing is
        // what null means, and conflating the two lets a client working from a stale group list wipe a
        // filing the user still has.
        if (groupId is not null && !_state.State.Groups.Exists(g => g.Id == groupId))
        {
            return;
        }

        conversations[existing] = conversations[existing] with { GroupId = groupId };
        await _state.WriteStateAsync();
    }

    // Order is the list position, kept contiguous so a delete or a reorder never leaves gaps that a
    // later append would collide with.
    private void Renumber()
    {
        var groups = _state.State.Groups;
        for (var i = 0; i < groups.Count; i++)
        {
            groups[i] = groups[i] with { Order = i };
        }
    }

    // Trims and length-caps a user-supplied title or group name. One helper, so a conversation title
    // and a group name can never drift apart on the length they allow.
    private static string Cap(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > MaxTitleLength ? trimmed[..MaxTitleLength] + "…" : trimmed;
    }
}
