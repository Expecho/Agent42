using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The past-chat tools (ADR 0111): <c>search_past_chats</c> and <c>read_past_chat</c> give the model the corpus the
/// sidebar search already gives the user (ADR 0061), so it can recall what was said in the user's other
/// conversations. Both read the chat-search PROJECTION, never another conversation's grain: a self-call would
/// deadlock this non-reentrant grain, activating another conversation connects its MCP servers, and an activation's
/// in-memory incognito turns are exactly what the index never holds.
///
/// <para>Every call is single-partition on this grain's user (the id <see cref="ChatSearchProjection"/> writes
/// with), and every hit is joined against the user's conversation list, which supplies the title and date and
/// drops anything the user no longer has. Results are stubbed out of the session after the turn
/// (<see cref="CompactToolPayloads"/>).</para>
/// </summary>
public sealed partial class AgentGrain
{
    // The descriptions are the policy the model obeys (rule 11); they live in PastChatLimits so a test can pin them.
    private AITool[] PastChatTools() =>
    [
        AIFunctionFactory.Create(
            SearchPastChatsAsync, name: PastChatLimits.SearchToolName, description: PastChatLimits.SearchDescription),
        AIFunctionFactory.Create(
            ReadPastChatAsync, name: PastChatLimits.ReadToolName, description: PastChatLimits.ReadDescription),
    ];

    private async Task<string> SearchPastChatsAsync(
        [Description("A few short, distinctive keywords, for example 'kubernetes ingress tls'.")] string query)
    {
        var terms = PastChatQuery.Terms(query);
        if (terms.Count == 0)
        {
            return "Give search_past_chats a few keywords of two or more letters to search for.";
        }

        if (PastChatBudgetSpent() is { } refusal)
        {
            return refusal;
        }

        try
        {
            return await SearchPastChatsCoreAsync(query, terms);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool must never brick the turn (the ReadDocumentAsync shape).
            _logger.LogError(ex, "Agent grain {GrainKey} failed to search past chats", this.GetPrimaryKeyString());
            return $"I could not search past chats: {ex.Message}";
        }
    }

    // The fetch → select → format half, split from the guard/catch shell above (cognitive_complexity_max 15).
    private async Task<string> SearchPastChatsCoreAsync(string query, IReadOnlyList<string> terms)
    {
        // Over-fetch to the index ceiling: the current conversation, orphans and the per-conversation cap all drop
        // hits AFTER the index ranked them, and a narrow fetch would leave too few.
        var ranked = await _chatSearchIndex.SearchRankedAsync(_userId, terms, ChatSearchLimits.MaxSearchResults);
        var known = await KnownConversationsAsync();
        var hits = PastChatQuery.Select(ranked, known, ConversationIdFromGrainKey(this.GetPrimaryKeyString()));

        return hits.Count == 0
            ? $"No other conversation matches '{query}'. Try different or fewer keywords."
            : PastChatQuery.FormatSearch(query, terms, hits, known);
    }

    private async Task<string> ReadPastChatAsync(
        [Description("The conversationId of a search_past_chats result.")] string conversationId,
        [Description("The ordinal of that result.")] int ordinal)
    {
        if (conversationId == ConversationIdFromGrainKey(this.GetPrimaryKeyString()))
        {
            return "That is this conversation, which you already have. Read past chats only for other conversations.";
        }

        if (PastChatBudgetSpent() is { } refusal)
        {
            return refusal;
        }

        try
        {
            return await ReadPastChatCoreAsync(conversationId, ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent grain {GrainKey} failed to read past chat {ConversationId}",
                this.GetPrimaryKeyString(), conversationId);
            return $"I could not read that past chat: {ex.Message}";
        }
    }

    private async Task<string> ReadPastChatCoreAsync(string conversationId, int ordinal)
    {
        // The conversation list is the ownership check on top of the partition: an id the user does not have (a
        // guess, another user's, a deleted chat's orphans) reads as not found.
        if (!(await KnownConversationsAsync()).TryGetValue(conversationId, out var info))
        {
            return $"No conversation with id '{conversationId}' was found. Use an id from a search_past_chats result.";
        }

        var (from, to) = PastChatQuery.Window(ordinal);
        var turns = await _chatSearchIndex.GetTurnsAsync(_userId, conversationId, from, to);

        return turns.Count == 0
            ? $"No turns around ordinal {ordinal} of '{info.Title}' are in the search index."
            : PastChatQuery.FormatRead(info, from, turns);
    }

    // The shared per-run budget (ADR 0106): counts this call, or returns the refusal once the turn's calls are spent.
    // Outside a run the budget reads exhausted, so the tools fail closed.
    private static string? PastChatBudgetSpent()
    {
        var context = TurnContext.CurrentOrClosed;
        if (context.PastChatCalls >= PastChatLimits.MaxCallsPerTurn)
        {
            return $"You have made {PastChatLimits.MaxCallsPerTurn} past-chat calls this turn, the limit. Answer from "
                + "what you found, and offer to look further on the user's next message.";
        }

        context.RecordPastChatCall();
        return null;
    }

    private async Task<Dictionary<string, ConversationInfo>> KnownConversationsAsync()
        => (await GrainFactory.GetGrain<IConversationIndexGrain>(_userId).GetConversationsAsync())
            .ToDictionary(info => info.Id, StringComparer.Ordinal);

    // Compaction's past-chat branch (ADR 0111): a SUCCESSFUL result (it starts with its header prefix) becomes a stub
    // built from the call's own arguments. A refusal stays verbatim, and a stub no longer starts with the header, so a
    // second pass leaves it alone (the CompactReadDocumentResult shape).
    private static void CompactPastChatResult(FunctionCallContent call, FunctionResultContent? result)
    {
        if (result is null || ToolText(result.Result) is not { } text)
        {
            return;
        }

        if (call.Name == PastChatLimits.SearchToolName && text.StartsWith(PastChatLimits.SearchHeaderPrefix, StringComparison.Ordinal))
        {
            result.Result = PastChatLimits.SearchStub(Argument(call, "query"));
        }
        else if (call.Name == PastChatLimits.ReadToolName && text.StartsWith(PastChatLimits.ReadHeaderPrefix, StringComparison.Ordinal))
        {
            // PartArgument reads a wire JsonElement or an in-process boxed int, as read_document's part does.
            var ordinal = call.Arguments?.TryGetValue("ordinal", out var value) == true ? PartArgument(value) : 0;
            result.Result = PastChatLimits.ReadStub(Argument(call, "conversationId"), ordinal);
        }
    }

    private static string Argument(FunctionCallContent call, string name)
        => call.Arguments?.TryGetValue(name, out var value) == true ? ToolText(value) ?? "" : "";
}
