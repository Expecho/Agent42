namespace Agent42.Agents;

/// <summary>
/// Caps on a conversation's shared state (ADR 0089). The state is persisted in the conversation's grain state,
/// which is one 2 MB Cosmos item (ADR 0004), and it is injected into the model's instructions every turn, so it is
/// kept small.
/// </summary>
public static class SharedStateLimits
{
    /// <summary>The largest serialized state, in UTF-8 bytes.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>The deepest nesting the state may have (the root object is depth 1).</summary>
    public const int MaxDepth = 8;

    /// <summary>The most JSON Patch operations one <c>update_shared_state</c> call may carry.</summary>
    public const int MaxOperations = 50;

    /// <summary>The name of the tool the model writes the state with.</summary>
    public const string UpdateToolName = "update_shared_state";
}
