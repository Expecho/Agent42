using System.ComponentModel;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// <c>search_past_chats</c> and <c>read_past_chat</c> for one fleet task (ADR 0144): the chat's tools (ADR 0112) over the
/// same chat-search projection and the same pure core (<see cref="PastChatQuery"/>: terms, ranking, formatting, the read
/// window), so the ranking is never forked. Every call is on the task's user, and every hit is joined against the user's
/// conversation list, which drops anything the user no longer has. The chat counts calls per turn on its
/// <c>TurnContext</c>; a fleet run has none, so the budget (<see cref="PastChatLimits.MaxCallsPerTurn"/>, per TASK here)
/// lives on this instance, which the catalog builds once per task and every agent of the task shares.
/// </summary>
public sealed class FleetPastChatTools
{
    /// <summary>Past-chat calls one fleet task may make, all agents together. The chat's per-turn cap, applied per task.</summary>
    public const int MaxCallsPerTask = PastChatLimits.MaxCallsPerTurn;

    private readonly IChatSearchIndex _index;
    private readonly IGrainFactory _grains;
    private readonly string _userId;
    private readonly ILogger _logger;
    private int _calls;

    public FleetPastChatTools(IChatSearchIndex index, IGrainFactory grains, string userId, ILogger logger)
    {
        _index = index;
        _grains = grains;
        _userId = userId;
        _logger = logger;
        Tools =
        [
            AIFunctionFactory.Create(SearchAsync, PastChatLimits.SearchToolName,
                "Search the user's conversations with you, to recall what was said or decided earlier. Search with a few short, "
                + "distinctive keywords, not a sentence. Results are snippets, most relevant first, each with a conversationId and "
                + $"ordinal: call {PastChatLimits.ReadToolName} with those to read the turns around a result before you rely on it. "
                + $"At most {MaxCallsPerTask} past-chat calls per task, for all agents together."),
            AIFunctionFactory.Create(ReadAsync, PastChatLimits.ReadToolName,
                $"Read the turns around one {PastChatLimits.SearchToolName} result, from one of the user's conversations. Pass the "
                + "conversationId and ordinal exactly as the search result gave them."),
        ];
    }

    public IReadOnlyList<AIFunction> Tools { get; }

    private async Task<string> SearchAsync(
        [Description("A few short, distinctive keywords, for example 'kubernetes ingress tls'.")] string query,
        CancellationToken cancellationToken)
    {
        var terms = PastChatQuery.Terms(query);
        if (terms.Count == 0)
        {
            return $"Give {PastChatLimits.SearchToolName} a few keywords of two or more letters to search for.";
        }

        if (BudgetSpent() is { } refusal)
        {
            return refusal;
        }

        try
        {
            var ranked = await _index.SearchRankedAsync(_userId, terms, ChatSearchLimits.MaxSearchResults, cancellationToken);
            var known = await KnownAsync();
            var hits = PastChatQuery.Select(ranked, known, "");
            return hits.Count == 0
                ? $"No conversation matches '{query}'. Try different or fewer keywords."
                : PastChatQuery.FormatSearch(query, terms, hits, known);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fleet past-chat search failed");
            return $"I could not search past chats: {ex.Message}";
        }
    }

    private async Task<string> ReadAsync(
        [Description("The conversationId of a search_past_chats result.")] string conversationId,
        [Description("The ordinal of that result.")] int ordinal,
        CancellationToken cancellationToken)
    {
        if (BudgetSpent() is { } refusal)
        {
            return refusal;
        }

        try
        {
            // The conversation list is the ownership check on top of the partition, as in the chat.
            if (!(await KnownAsync()).TryGetValue(conversationId, out var info))
            {
                return $"No conversation with id '{conversationId}' was found. Use an id from a {PastChatLimits.SearchToolName} result.";
            }

            var (from, to) = PastChatQuery.Window(ordinal);
            var turns = await _index.GetTurnsAsync(_userId, conversationId, from, to, cancellationToken);
            return turns.Count == 0
                ? $"No turns around ordinal {ordinal} of '{info.Title}' are in the search index."
                : PastChatQuery.FormatRead(info, from, turns);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fleet past-chat read failed");
            return $"I could not read that past chat: {ex.Message}";
        }
    }

    // Counts this call, or refuses once the task's calls are spent. Agents of one task run concurrently.
    private string? BudgetSpent()
        => Interlocked.Increment(ref _calls) > MaxCallsPerTask
            ? $"This task has made {MaxCallsPerTask} past-chat calls, the limit. Work with what was found."
            : null;

    private async Task<Dictionary<string, ConversationInfo>> KnownAsync()
        => (await _grains.GetGrain<IConversationIndexGrain>(_userId).GetConversationsAsync())
            .ToDictionary(info => info.Id, StringComparer.Ordinal);
}
