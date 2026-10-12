namespace Agent42.Backend;

/// <summary>
/// The question slot of one running fleet task (ADR 0097): plan approvals, Copilot permissions and MCP elicitations
/// are shown on the page one at a time, as a <c>prompt</c> event, and the answer arrives through
/// <c>POST /api/fleet/tasks/{id}/answer</c>. Agents of teams running at once take turns. A stopped task cancels its
/// open question.
/// </summary>
public sealed class FleetQuestions(IFleetEventSink events) : IFleetInteraction
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _gate = new();
    private TaskCompletionSource<string>? _open;

    public bool HasOpenQuestion
    {
        get
        {
            lock (_gate)
            {
                return _open is not null;
            }
        }
    }

    public async Task<string> AskAsync(string question, IReadOnlyList<FleetChoice> choices, string freeTextHint, CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken);
        try
        {
            var answered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _open = answered;
            }

            using var cancellation = cancellationToken.Register(() =>
            {
                lock (_gate)
                {
                    _open = null;
                }

                answered.TrySetCanceled(cancellationToken);
            });

            events.Publish("prompt", new { question = question.Trim(), choices = choices.Select(c => new { answer = c.Answer, label = c.Label }), freeTextHint });
            try
            {
                var answer = await answered.Task;
                events.Publish("prompt.answered", new { answer });
                return answer;
            }
            catch (OperationCanceledException)
            {
                events.Publish("prompt.answered", new { answer = "" });
                throw;
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Answers the open question; false when none is open.</summary>
    public bool Answer(string answer)
    {
        TaskCompletionSource<string>? question;
        lock (_gate)
        {
            question = _open;
            _open = null;
        }

        return question?.TrySetResult((answer ?? "").Trim()) == true;
    }
}
