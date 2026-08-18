using Agent42.Agents.Push;

namespace Agent42.Agents.Conversations;

/// <summary>
/// Web Push on an away reply (ADR 0028). Called only from the two ASYNCHRONOUS reply paths — a fired
/// reminder and an agent-run — and never from <c>ChatCoreAsync</c>, the live stream the user is
/// already watching. That single fact is how "user is not online" is approximated; a server-side
/// presence check was considered and rejected.
/// </summary>
public sealed partial class AgentGrain
{
    // Fires an away-reply push (ADR 0028) for a completed ASYNC-path reply — a fired reminder or an
    // agent-run turn. Title is the bound agent's name, or "Agent 42" (the manifest product name, a
    // user-facing display title — deliberately NOT the internal "Agent42" persona option) when the
    // conversation has no bound agent, e.g. a scheduled prompt on a plain chat. The sender is best-effort
    // and swallows all failures, so this is a safe inline await even on the non-reentrant grain (it is
    // internally time-bounded). NEVER call this from ChatCoreAsync/ChatAsync — the live path must not push.
    private Task SendAwayReplyPushAsync() =>
        _pushSender.SendAsync(
            _userId,
            new PushMessage(
                _state.State.AgentName ?? "Agent 42",
                "Replied while you were away.",
                ConversationIdFromGrainKey(this.GetPrimaryKeyString())));

}
