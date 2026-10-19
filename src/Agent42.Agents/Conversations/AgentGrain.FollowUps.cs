using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Follow-up suggestion prompts (ADR 0036): one extra, best-effort model call after an
/// <b>interactive</b> reply, streamed as a <c>Suggestions</c> event before <c>Done</c>. Gated by the run's
/// <see cref="TurnContext.Interactive"/> (ADR 0106), so the agent-run and reminder drains of the same
/// <c>ChatCoreAsync</c> never spend the call.
/// </summary>
public sealed partial class AgentGrain
{
    // Generates up to 3 short follow-up prompts the user might click to continue, via a second model call on
    // this conversation's model. It STREAMS (GetStreamingResponseAsync) and aggregates the text — the same
    // path the reply already uses on the SAME cached client instance (ChatClientFactory GetOrAdd). This is
    // deliberate: the non-streaming GetResponseAsync is otherwise unexercised on the Foundry Responses adapter
    // (ADR 0014), where it was returning nothing in prod; streaming behaves exactly like the reply that works.
    // BEST-EFFORT and side-effect-free: it builds a THROWAWAY message list (never _session), writes no state,
    // and swallows every failure into null so the surrounding turn is never affected. The try/catch lives here
    // — not in the iterator ChatCoreAsync — because C# forbids `yield` inside a try with a catch.
    private async Task<IReadOnlyList<string>?> GenerateFollowUpsAsync(string userMessage, string replyText)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You suggest what the user might ask next. Given the last exchange, propose up to 3 short, "
                    + "distinct follow-up questions or requests the USER could send to continue in English. Write each on "
                    + "its own line, phrased in the user's voice, with no numbering, bullets, or quotes. Output "
                    + "only the lines."),
                new(ChatRole.User,
                    $"User asked:\n{userMessage}\n\nAssistant replied:\n{replyText}\n\nSuggest the follow-ups:"),
            };

            var text = new StringBuilder();
            // Reasoning is switched OFF explicitly, not left null (ADR 0071). The factory wraps a declared
            // reasoning model's client in a ConfigureOptions that does `Reasoning ??= Summary`, and that
            // client is SHARED with the turn — so a null here would silently make every interactive turn on
            // a reasoning model pay for a second reasoning summary whose tokens this loop then discards
            // (it appends TextContent only). Measured: Output = None sends no `reasoning` field at all, the
            // same request as omitting it, so this costs nothing on a non-reasoning model.
            var options = new ChatOptions
            {
                MaxOutputTokens = 400,
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.None },
            };

            await foreach (var update in _chatClientFactory.GetClient(_modelId)
                .GetStreamingResponseAsync(messages, options))
            {
                foreach (var content in update.Contents)
                {
                    if (content is TextContent { Text: { Length: > 0 } chunk })
                    {
                        text.Append(chunk);
                    }
                }
            }

            var picks = ParseFollowUps(text.ToString());

            _logger.LogInformation(
                "Agent grain {GrainKey} follow-up suggestions: {TextLength} chars of model output → {Count} prompt(s)",
                this.GetPrimaryKeyString(), text.Length, picks.Count);

            return picks.Count > 0 ? picks : null;
        }
        catch (Exception ex)
        {
            // Never fatal — the reply already streamed. Warn (not debug) so a Responses-path failure is
            // visible in prod telemetry rather than silently dropping the suggestions for the turn.
            _logger.LogWarning(ex, "Agent grain {GrainKey} could not generate follow-up suggestions", this.GetPrimaryKeyString());
            return null;
        }
    }

    // Turns the model's raw suggestion output into up to 3 clean prompts. One-per-line is the instructed
    // shape; if a model ignores that and returns a single blob, fall back to splitting on sentence
    // terminators so non-empty output still yields at least one chip.
    private static IReadOnlyList<string> ParseFollowUps(string text)
    {
        var lines = SplitAndClean(text, '\n');
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            // Blob response: split on sentence terminators (kept via a lookbehind-style regex).
            lines = FollowUpSentenceSplit()
                .Split(text)
                .Select(StripLeadingListMarker)
                .Where(line => line.Length > 0)
                .ToList();
        }

        return lines.Take(3).ToArray();
    }

    private static List<string> SplitAndClean(string text, char separator)
        => text
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripLeadingListMarker)
            .Where(line => line.Length > 0)
            .ToList();

    // Strips a leading list marker a model may add despite the instruction ("- ", "* ", "1. ", "1) ") and
    // any surrounding quotes, so a suggestion renders clean in the composer.
    private static string StripLeadingListMarker(string line)
    {
        line = FollowUpMarkerRegex().Replace(line.Trim(), "");
        return line.Trim().Trim('"').Trim();
    }

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+")]
    private static partial Regex FollowUpMarkerRegex();

    // Splits a single-blob suggestion response into candidate lines at sentence terminators (?/!) or a
    // question mark run, keeping the terminator with its sentence.
    [GeneratedRegex(@"(?<=[?!])\s+")]
    private static partial Regex FollowUpSentenceSplit();
}
