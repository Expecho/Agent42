using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The past-chat tools (ADR 0112): <c>search_past_chats</c> and <c>read_past_chat</c> give the model the corpus the
/// sidebar search already gives the user (ADR 0061), so it can recall what was said in the user's other
/// conversations. Both read the chat-search PROJECTION, never another conversation's grain: a self-call would
/// deadlock this non-reentrant grain, activating another conversation connects its MCP servers, and an activation's
/// in-memory incognito turns are exactly what the index never holds.
///
/// <para>Every call is single-partition on this grain's user (the id <see cref="ChatSearchProjection"/> writes
/// with), and every hit is joined against the user's conversation list, which supplies the title and date and
/// drops anything the user no longer has. Results are stubbed out of the session after the turn
/// (<c>AgentGrain.CompactPastChatResult</c>).</para>
///
/// <para>Built per grain activation with <c>new</c> (ADR 0106's pattern, extended by ADR 0147); public only
/// because this repo has no <c>InternalsVisibleTo</c>. It holds the index and the user and conversation ids, no grain
/// state; the per-run budget is on the <see cref="TurnContext"/>.</para>
/// </summary>
public sealed class PastChatTools(
    IChatSearchIndex chatSearchIndex, IGrainFactory grains, string userId, string currentConversationId, string grainKey, ILogger logger)
{
    // The descriptions are the policy the model obeys (rule 11); they live in PastChatLimits so a test can pin them.
    public AITool[] Functions() =>
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
            logger.LogError(ex, "Agent grain {GrainKey} failed to search past chats", grainKey);
            return $"I could not search past chats: {ex.Message}";
        }
    }

    // The fetch → select → format half, split from the guard/catch shell above (cognitive_complexity_max 15).
    private async Task<string> SearchPastChatsCoreAsync(string query, IReadOnlyList<string> terms)
    {
        // Over-fetch to the index ceiling: the current conversation, orphans and the per-conversation cap all drop
        // hits AFTER the index ranked them, and a narrow fetch would leave too few.
        var ranked = await chatSearchIndex.SearchRankedAsync(userId, terms, ChatSearchLimits.MaxSearchResults);
        var known = await KnownConversationsAsync();
        var hits = PastChatQuery.Select(ranked, known, currentConversationId);

        return hits.Count == 0
            ? $"No other conversation matches '{query}'. Try different or fewer keywords."
            : PastChatQuery.FormatSearch(query, terms, hits, known);
    }

    private async Task<string> ReadPastChatAsync(
        [Description("The conversationId of a search_past_chats result.")] string conversationId,
        [Description("The ordinal of that result.")] int ordinal)
    {
        if (conversationId == currentConversationId)
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
            logger.LogError(ex, "Agent grain {GrainKey} failed to read past chat {ConversationId}",
                grainKey, conversationId);
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
        var turns = await chatSearchIndex.GetTurnsAsync(userId, conversationId, from, to);

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
        => (await grains.GetGrain<IConversationIndexGrain>(userId).GetConversationsAsync())
            .ToDictionary(info => info.Id, StringComparer.Ordinal);
}
