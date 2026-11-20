using System.ClientModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Some reasoning models reject the reasoning-summary request outright (<c>gpt-6.2-astra</c>: HTTP 400,
/// <c>unsupported_parameter</c>, <c>reasoning.summary</c>), and ticking "reasoning" for a model sends it (ADR 0071).
/// This client retries ONCE without the summary and remembers the model, so the cost is one extra call per model per
/// process, not one per turn.
///
/// <para><b>Two shapes, both measured on the pinned stack (<c>ReasoningSummaryFallbackTests</c>).</b> A plain HTTP 400
/// throws <see cref="ClientResultException"/> before any update; a Responses stream can instead carry the same
/// rejection as an <c>error</c> event, which surfaces as an <see cref="ErrorContent"/> update after an empty one. Both
/// are recognised only when nothing but empty updates came first, so a reply that already started streaming is never
/// replayed.</para>
///
/// <para>Only the summary is dropped: the effort the caller set is kept. The remembered set is process-wide because the
/// per-user factories are rebuilt, and a restart forgets it (one retry per model again, never a wrong answer).</para>
/// </summary>
public sealed class ReasoningSummaryFallbackChatClient(IChatClient inner, string modelKey) : DelegatingChatClient(inner)
{
    private static readonly ConcurrentDictionary<string, byte> Rejected = new();

    /// <summary>The key one model is remembered under: provider kind, endpoint and model id, never a user.</summary>
    public static string KeyFor(ProviderKind kind, string? endpoint, string model) => $"{kind}|{endpoint}|{model}";

    /// <summary>Whether this model has already rejected a summary request in this process.</summary>
    public static bool IsRemembered(string modelKey) => Rejected.ContainsKey(modelKey);

    /// <summary>Forgets every remembered model. For tests, which share the process.</summary>
    public static void ForgetAll() => Rejected.Clear();

    /// <summary>True for the provider's refusal of the summary request, in either text or error-event form.</summary>
    public static bool IsSummaryRejection(string? message) =>
        message is not null
        && message.Contains("reasoning.summary", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not supported", StringComparison.OrdinalIgnoreCase));

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        if (!AsksForSummary(options))
        {
            return await base.GetResponseAsync(messageList, options, cancellationToken);
        }

        if (!IsRemembered(modelKey))
        {
            try
            {
                return await base.GetResponseAsync(messageList, options, cancellationToken);
            }
            catch (ClientResultException ex) when (IsSummaryRejection(ex.Message))
            {
                Remember();
            }
        }

        return await base.GetResponseAsync(messageList, WithoutSummary(options), cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        if (!AsksForSummary(options))
        {
            await foreach (var update in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        if (!IsRemembered(modelKey))
        {
            var attempt = new Attempt();
            await foreach (var update in FirstAttemptAsync(messageList, options, attempt, cancellationToken))
            {
                yield return update;
            }

            if (!attempt.Rejected)
            {
                yield break;
            }

            Remember();
        }

        await foreach (var update in base.GetStreamingResponseAsync(messageList, WithoutSummary(options), cancellationToken))
        {
            yield return update;
        }
    }

    // The outcome rides a class because the producer is an iterator, which cannot hand out an out parameter.
    private sealed class Attempt
    {
        public bool Started { get; set; }

        public bool Rejected { get; set; }
    }

    // The attempt that asks for the summary. Updates with no content yet (the Responses `created` event) are HELD, and
    // released only once something real arrives: the rejection comes right after them, and releasing them first would
    // hand the caller a half-started stream it then has to be given a second copy of.
    private async IAsyncEnumerable<ChatResponseUpdate> FirstAttemptAsync(
        IList<ChatMessage> messages, ChatOptions? options, Attempt attempt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var held = new List<ChatResponseUpdate>();
        await using var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (await MoveNextOrRejectAsync(enumerator, attempt))
        {
            var current = enumerator.Current;
            if (!attempt.Started && current.Contents.OfType<ErrorContent>().Any(e => IsSummaryRejection(e.Message)))
            {
                attempt.Rejected = true;
                yield break;
            }

            attempt.Started |= current.Contents.Count > 0;
            if (!attempt.Started)
            {
                held.Add(current);
                continue;
            }

            foreach (var heldUpdate in held)
            {
                yield return heldUpdate;
            }

            held.Clear();
            yield return current;
        }

        foreach (var heldUpdate in held)
        {
            yield return heldUpdate;
        }
    }

    // False at the end of the stream, or when the provider refused the summary before anything streamed.
    private static async Task<bool> MoveNextOrRejectAsync(IAsyncEnumerator<ChatResponseUpdate> enumerator, Attempt attempt)
    {
        try
        {
            return await enumerator.MoveNextAsync();
        }
        catch (ClientResultException ex) when (!attempt.Started && IsSummaryRejection(ex.Message))
        {
            attempt.Rejected = true;
            return false;
        }
    }

    private void Remember() => Rejected[modelKey] = 0;

    private static bool AsksForSummary(ChatOptions? options) =>
        options?.Reasoning is { Output: not null and not ReasoningOutput.None };

    // A clone, never a mutation: the caller's ChatOptions is shared by ChatClientFactory.Build's ConfigureOptions.
    private static ChatOptions WithoutSummary(ChatOptions? options)
    {
        var clone = options!.Clone();
        clone.Reasoning = clone.Reasoning is { Effort: { } effort } ? new ReasoningOptions { Effort = effort } : null;
        return clone;
    }
}
