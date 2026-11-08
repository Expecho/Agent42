using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The fleet's clarify phase (ADR 0118): the interviewer asks one question at a time through the task's question slot
/// and ends with a brief. The cap, the "start now" choice, a model that never clears and a stop are what keep it from
/// grilling forever or blocking a task.
/// </summary>
public sealed class FleetClarifierTests
{
    private const string Request = "Write a haiku";

    private static (string Name, Dictionary<string, object?> Arguments) Ask(string question, params string[] choices)
        => ("ask_user", choices.Length == 0
            ? new() { ["question"] = question }
            : new() { ["question"] = question, ["choices"] = choices });

    private static (string Name, Dictionary<string, object?> Arguments) Clear(string brief)
        => ("requirements_clear", new() { ["brief"] = brief });

    private static FakeChatClient.FleetScript Script(params (string Name, Dictionary<string, object?> Arguments)[] calls)
        => new([], clarifyCalls: calls);

    // The request carries the script's marker, so the fake knows which script the interviewer follows.
    private static async Task<(string Brief, FleetRunHarness Harness)> RunAsync(
        FakeChatClient.FleetScript script, FleetOptions? options = null, string[]? answers = null, string files = "", string? previous = null)
    {
        var harness = new FleetRunHarness(options, answers: answers ?? []);
        var brief = await new FleetClarifier(harness.Run, FakeChatClient.FleetTask(Request, script), files, previous).RunAsync(CancellationToken.None);
        return (brief, harness);
    }

    [Fact]
    public async Task AsksThenClears_ReturnsBrief()
    {
        var script = Script(Ask("Who reads it?"), Ask("Which format?", "Plain", "HTML"), Clear("Brief: a haiku for engineers, plain text"));

        var (brief, harness) = await RunAsync(script, answers: ["Engineers", "Plain"]);

        Assert.StartsWith("Brief: a haiku for engineers, plain text", brief);
        Assert.Equal(["Who reads it?", "Which format?"], harness.Interaction.Questions);
        var done = Assert.Single(harness.Events.Of("clarify.done"));
        Assert.Equal(2, done.GetProperty("questions").GetInt32());
        Assert.True(done.GetProperty("cleared").GetBoolean());
        Assert.Single(harness.Events.Of("clarify.started"));
        Assert.Contains(Request, Assert.Single(script.ClarifyInputs));
    }

    /// <summary>Every question carries the fixed "start now" choice after the interviewer's own, at most four.</summary>
    [Fact]
    public async Task Question_OffersChoices_AndTheStartChoiceLast()
    {
        var (_, harness) = await RunAsync(Script(Ask("Pick", "a", "b", "c", "d", "e", " ", "a"), Clear("Brief")));

        var offered = Assert.Single(harness.Interaction.Choices);
        Assert.Equal(["a", "b", "c", "d", FleetClarifier.StartAnswer], offered.Select(c => c.Answer));
        Assert.Equal("Enough, start the task", offered[^1].Label);
    }

    [Fact]
    public async Task CapReached_RefusesFurtherQuestions()
    {
        var script = Script(Ask("One?"), Ask("Two?"), Ask("Three?"), Clear("Brief after one question"));

        var (brief, harness) = await RunAsync(script, new FleetOptions { MaxClarifyingQuestions = 1 });

        Assert.Equal(["One?"], harness.Interaction.Questions);
        Assert.StartsWith("Brief after one question", brief);
        Assert.Equal(1, Assert.Single(harness.Events.Of("clarify.done")).GetProperty("questions").GetInt32());
    }

    [Fact]
    public async Task CapBelowOne_CountsAsOne()
    {
        var (_, harness) = await RunAsync(Script(Ask("One?"), Ask("Two?"), Clear("Brief")), new FleetOptions { MaxClarifyingQuestions = 0 });

        Assert.Equal(["One?"], harness.Interaction.Questions);
    }

    [Fact]
    public async Task StartChoice_StopsAsking()
    {
        var script = Script(Ask("One?"), Ask("Two?"), Clear("Brief so far"));

        var (brief, harness) = await RunAsync(script, answers: [FleetClarifier.StartAnswer]);

        Assert.Equal(["One?"], harness.Interaction.Questions);
        Assert.StartsWith("Brief so far", brief);
        var done = Assert.Single(harness.Events.Of("clarify.done"));
        Assert.True(done.GetProperty("startedNow").GetBoolean());
    }

    [Fact]
    public async Task NoClear_FallsBackToTheRequest()
    {
        var (brief, harness) = await RunAsync(Script(Ask("One?")));

        Assert.StartsWith(Request, brief);
        Assert.False(Assert.Single(harness.Events.Of("clarify.done")).GetProperty("cleared").GetBoolean());
    }

    [Fact]
    public async Task EmptyQuestion_IsRefusedWithoutAskingTheUser()
    {
        var (_, harness) = await RunAsync(Script(("ask_user", new() { ["question"] = "  " }), Clear("Brief")));

        Assert.Empty(harness.Interaction.Questions);
        Assert.Equal(0, Assert.Single(harness.Events.Of("clarify.done")).GetProperty("questions").GetInt32());
    }

    /// <summary>Caps refuse rather than truncate (the ADR 0102 convention): the interviewer is told to shorten the brief.</summary>
    [Fact]
    public async Task OverlongBrief_IsRefused_ThenShortOneAccepted()
    {
        var script = Script(Clear(new string('x', FleetClarifier.MaxBriefLength + 1)), Clear("Short brief"));

        var (brief, _) = await RunAsync(script);

        Assert.StartsWith("Short brief", brief);
    }

    [Fact]
    public async Task Files_AndPreviousRequest_ReachTheInterviewer()
    {
        var script = Script(Clear("Brief"));

        await RunAsync(script, files: "\n\nAttached files: inputs/spec.pdf", previous: "Earlier haiku task");

        var input = Assert.Single(script.ClarifyInputs);
        Assert.Contains("inputs/spec.pdf", input);
        Assert.Contains("follow-up on an earlier task of the user: Earlier haiku task", input);
    }

    /// <summary>A stop cancels the open question; the clarifier must let that through, not turn it into a fallback.</summary>
    [Fact]
    public async Task Cancelled_Propagates()
    {
        var harness = new FleetRunHarness();
        harness.Interaction.Cancel = true;
        var clarifier = new FleetClarifier(harness.Run, FakeChatClient.FleetTask(Request, Script(Ask("One?"), Clear("Brief"))), "", null);
        // As the service does: a stop cancels the budget, and the budget's token is the clarifier's. (The function-invoking
        // client only rethrows a cancellation when its own token is cancelled, which is why the token matters.)
        harness.Budget.Cancel("stopped by the user");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clarifier.RunAsync(harness.Budget.Token));

        Assert.Empty(harness.Events.Of("clarify.done"));
    }
}
