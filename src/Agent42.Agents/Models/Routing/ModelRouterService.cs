using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The pure routing logic (ADR 0137): filter the candidates by capability, describe them to the classifier under
/// positional keys, and apply the sticky rule to its probabilities. The classifier is advice; the capability filter
/// is the check. Any failure keeps the current model.
/// </summary>
public sealed class ModelRouterService(
    IReadOnlyList<RouterCandidate> candidates,
    IDecisionClient client,
    ModelRouterOptions options,
    ILogger logger) : IModelRouter
{
    public async Task<RouterDecision?> RouteAsync(RouterTurn turn, CancellationToken cancellationToken = default)
    {
        var eligible = FilterCandidates(candidates, turn);
        if (eligible.Count == 0)
            return null;

        // No chat model can run this turn (an attached image and no vision model, tools on and no tools model): nothing is routed,
        // however many image models survive. A picture is never made without a chat alternative AND a classifier answer that
        // clears the higher bar (ADR 0138).
        if (eligible.All(c => c.IsImageGeneration))
            return null;
        if (eligible.Count == 1)
            return ToDecision(eligible[0], turn.CurrentModelId);

        var keyed = eligible.Select((c, i) => (Key: $"m{i + 1}", Candidate: c)).ToList();
        var optionText = keyed.ToDictionary(k => k.Key, k => Describe(k.Candidate));
        optionText[options.UnknownKey] = "Cannot tell which model fits, or the message needs no particular strength";

        IReadOnlyDictionary<string, double>? probabilities;
        try
        {
            probabilities = await client.ChooseAsync(BuildState(turn), optionText, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Model router call failed; keeping the current model");
            return null;
        }

        if (probabilities is null)
            return null;

        var chosen = Decide(keyed, probabilities, turn.CurrentModelId);
        return chosen is null ? null : ToDecision(chosen, turn.CurrentModelId);
    }

    /// <summary>The fixed context line (never user text) put before the message when the last reply was a picture.</summary>
    public const string PreviousReplyWasImageLine = "Context: the assistant's previous reply was a generated picture.";

    private string BuildState(RouterTurn turn)
    {
        var message = Trim(turn.Message, options.MaxMessageChars);
        return turn.PreviousReplyWasImage ? $"{PreviousReplyWasImageLine}\n{message}" : message;
    }

    /// <summary>The hard capability filter: the router never sees an option the turn cannot run on.</summary>
    public static IReadOnlyList<RouterCandidate> FilterCandidates(IEnumerable<RouterCandidate> all, RouterTurn turn)
        => all.Where(c => !c.CopilotOnly && IsEligible(c, turn)).ToList();

    // An image generator declares no tools and CONSUMES images (ADR 0020), so only chat models face those two filters.
    private static bool IsEligible(RouterCandidate c, RouterTurn turn)
        => c.IsImageGeneration
           || (c.IsChat && (!turn.ToolsEnabled || c.SupportsTools) && (!turn.HasImages || c.SupportsVision));

    /// <summary>Layer order: the user's hint, then a public model card, then a description from flags (works alone).</summary>
    public static string Describe(RouterCandidate c)
    {
        if (!string.IsNullOrWhiteSpace(c.Hint))
            return c.Hint.Trim();
        if (!string.IsNullOrWhiteSpace(c.CardText))
            return c.CardText.Trim();
        if (c.IsImageGeneration)
            return $"{c.DisplayName}: image generator, creates or edits a picture from a prompt";
        var parts = new List<string> { "chat model" };
        if (c.SupportsTools)
            parts.Add("calls tools");
        if (c.SupportsVision)
            parts.Add("reads images");
        return $"{c.DisplayName}: {string.Join(", ", parts)}";
    }

    public static string Trim(string message, int maxChars)
    {
        var text = message.Trim();
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    private RouterCandidate? Decide(
        List<(string Key, RouterCandidate Candidate)> keyed, IReadOnlyDictionary<string, double> probabilities, string? currentId)
    {
        var scored = keyed.Where(k => probabilities.ContainsKey(k.Key))
            .Select(k => (k.Candidate, P: probabilities[k.Key])).OrderByDescending(s => s.P).ToList();
        if (scored.Count == 0)
        {
            logger.LogWarning("Model router answered with no known option key; keeping the current model");
            return null;
        }

        var unknownP = probabilities.GetValueOrDefault(options.UnknownKey);
        return DecideImage(scored, unknownP)
            ?? DecideChat(scored.Where(s => !s.Candidate.IsImageGeneration).ToList(), unknownP, currentId);
    }

    /// <summary>ADR 0138: the best image option wins only at the higher bar AND as the largest answer, "cannot tell" included.</summary>
    private RouterCandidate? DecideImage(List<(RouterCandidate Candidate, double P)> scored, double unknownP)
    {
        var bestImage = scored.FirstOrDefault(s => s.Candidate.IsImageGeneration);
        if (bestImage.Candidate is null)
            return null;
        var largest = Math.Max(scored[0].P, unknownP);
        return bestImage.P >= options.ImageMinProbability && bestImage.P >= largest ? bestImage.Candidate : null;
    }

    /// <summary>ADR 0137's sticky rule over the chat options only. An image model is never the "current" model.</summary>
    private RouterCandidate? DecideChat(List<(RouterCandidate Candidate, double P)> chat, double unknownP, string? currentId)
    {
        if (chat.Count == 0)
            return null;

        var best = chat[0];
        var currentScore = chat.FirstOrDefault(s => s.Candidate.Id == currentId);

        // The current model survived the filter: a "cannot tell" win, or too weak/narrow a win, keeps it.
        if (currentScore.Candidate is not null)
        {
            if (unknownP > best.P || best.Candidate.Id == currentId)
                return currentScore.Candidate;
            var clearWin = best.P >= options.MinProbability && best.P - currentScore.P >= options.Margin;
            return clearWin ? best.Candidate : currentScore.Candidate;
        }

        // The current model cannot run this turn (filtered out): take the best option that can.
        return best.Candidate;
    }

    private static RouterDecision ToDecision(RouterCandidate c, string? currentId)
        => new(c.Id, c.DisplayName, !string.Equals(c.Id, currentId, StringComparison.Ordinal), c.IsImageGeneration);
}
