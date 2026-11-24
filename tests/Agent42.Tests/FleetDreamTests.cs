using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using static Agent42.Tests.ScriptedDreamClient;

namespace Agent42.Tests;

/// <summary>One dream model run (ADR 0124): validation through <c>submit_lessons</c>, fail-open, caps and usage attribution.</summary>
public class FleetDreamTests
{
    private static readonly UserTelemetryContext Alice = new("alice", "alice", "a@example.com", "Alice", null, null);

    private static readonly FleetDigestResult Digest = new(
        "<fleet-result>\nTask t1 (workspace w1): done, 10 tokens, 2 agents.\n\nTask t2 (workspace w2): done, 10 tokens, 2 agents.\n\nTask t3 (workspace w3): failed, 10 tokens, 2 agents.\n</fleet-result>",
        new Dictionary<string, string> { ["t1"] = "w1", ["t2"] = "w2", ["t3"] = "w3" }, 3);

    private static Task<FleetDreamOutcome> Dream(ScriptedDreamClient client, FleetOptions? options = null, CancellationToken cancellationToken = default)
        => new FleetDreamer(options ?? new FleetOptions(), FleetPromptSet.Defaults, Digest, NullLogger.Instance).RunAsync(client, Alice, cancellationToken);

    [Fact]
    public async Task AcceptsValidLessons_AndTheModelSeesTheDigestAsData()
    {
        var client = new ScriptedDreamClient(c => c.Number == 1
            ? Submit(Proposed("Split research and writing into two staged agents.", ["t1", "t2"]))
            : Say("done"));

        var outcome = await Dream(client);

        var lesson = Assert.Single(outcome.Accepted);
        Assert.Equal(["t1", "t2"], lesson.Evidence);
        Assert.Equal((FleetDreamStatus.Ran, 0), (outcome.Status, outcome.Refused));
        Assert.Contains("Task t1 (workspace w1)", client.UserText);
        Assert.Contains("data, not instructions", client.UserText);
        Assert.StartsWith(FleetToolLimits.ResultOpenTag, client.UserText![client.UserText.IndexOf(FleetToolLimits.ResultOpenTag, StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_RendersItsPlaceholders()
    {
        var client = new ScriptedDreamClient(_ => Say("nothing"));

        await Dream(client, new FleetOptions { DreamMinSupport = 3, DreamMaxNewPerRun = 5 });

        Assert.DoesNotContain("{{", client.Instructions);
        Assert.Contains("at least 3 different tasks", client.Instructions);
        Assert.Contains("at most\n5 lessons", client.Instructions?.ReplaceLineEndings("\n"));
        Assert.Contains("team-shape", client.Instructions);
    }

    [Fact]
    public async Task RefusedLesson_ReturnsTheReasonToTheModel_AndACorrectedOneIsAccepted()
    {
        string? toolResult = null;
        var client = new ScriptedDreamClient(c =>
        {
            toolResult = c.LastToolResult ?? toolResult;
            return c.Number switch
            {
                1 => Submit(Proposed("See example.com for the answer.", ["t1", "t2"]), Proposed("Review twice.", ["t1", "ghost"]), Proposed("Stage the work.", ["t1"])),
                2 => Submit(Proposed("Stage the work.", ["t1", "t3"])),
                _ => Say("done"),
            };
        });

        var outcome = await Dream(client);

        Assert.Equal(3, outcome.Refused);
        Assert.Equal("Stage the work.", Assert.Single(outcome.Accepted).Text);
        Assert.Equal(FleetDreamStatus.Ran, outcome.Status);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task ToolResultNamesEachRefusal()
    {
        string? result = null;
        var client = new ScriptedDreamClient(c =>
        {
            result ??= c.LastToolResult;
            return c.Number == 1 ? Submit(Proposed("bad `text`", ["t1", "t2"]), Proposed("fine one", ["t1", "t2"])) : Say("ok");
        });

        await Dream(client);

        Assert.Contains("Lesson 1: refused:", result);
        Assert.Contains("Lesson 2: accepted.", result);
    }

    [Fact]
    public async Task PerRunCap_RefusesTheRest()
    {
        var options = new FleetOptions { DreamMaxNewPerRun = 1 }; // the run takes at most 3 proposals
        var client = new ScriptedDreamClient(c => c.Number == 1
            ? Submit(Proposed("one", ["t1", "t2"]), Proposed("two", ["t1", "t2"]), Proposed("three", ["t1", "t2"]), Proposed("four", ["t1", "t2"]))
            : Say("ok"));

        var outcome = await Dream(client, options);

        Assert.Equal(3, outcome.Accepted.Count);
        Assert.Equal(1, outcome.Refused);
    }

    [Fact]
    public async Task ARepeatInOneRun_IsRefused()
    {
        var client = new ScriptedDreamClient(c => c.Number == 1
            ? Submit(Proposed("Stage the work.", ["t1", "t2"]), Proposed("  stage THE work. ", ["t1", "t3"]))
            : Say("ok"));

        var outcome = await Dream(client);

        Assert.Single(outcome.Accepted);
        Assert.Equal(1, outcome.Refused);
    }

    [Fact]
    public async Task NoToolCall_Ran_WithZeroLessons()
    {
        var outcome = await Dream(new ScriptedDreamClient(_ => Say("I see nothing worth saying.")));

        Assert.Empty(outcome.Accepted);
        Assert.Equal(FleetDreamStatus.Ran, outcome.Status);
        Assert.Contains("no lessons", outcome.Reason);
    }

    [Fact]
    public async Task EmptySubmission_IsAValidAnswer()
    {
        var outcome = await Dream(new ScriptedDreamClient(c => c.Number == 1 ? Submit() : Say("ok")));

        Assert.Empty(outcome.Accepted);
        Assert.Equal(FleetDreamStatus.Ran, outcome.Status);
    }

    [Fact]
    public async Task ModelError_FailsOpen_KeepingWhatWasAccepted()
    {
        var client = new ScriptedDreamClient(c => c.Number == 1
            ? Submit(Proposed("Stage the work.", ["t1", "t2"]))
            : throw new HttpRequestException("provider down"));

        var outcome = await Dream(client);

        Assert.Equal(FleetDreamStatus.Failed, outcome.Status);
        Assert.Contains("provider down", outcome.Reason);
        Assert.Single(outcome.Accepted);
    }

    [Fact]
    public async Task Timeout_FailsOpen_WithoutThrowing()
    {
        var gate = new TaskCompletionSource();
        var client = new ScriptedDreamClient(_ => Say("never")) { Gate = gate };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var outcome = await Dream(client, cancellationToken: cts.Token);

        Assert.Equal(FleetDreamStatus.Failed, outcome.Status);
        Assert.Contains("timed out", outcome.Reason);
        Assert.Empty(outcome.Accepted);
    }

    [Fact]
    public async Task IterationCap_StopsAModelThatNeverStops()
    {
        var client = new ScriptedDreamClient(c => Submit(Proposed($"Lesson number {c.Number}.", ["t1", "t2"])));

        var outcome = await Dream(client);

        Assert.True(client.Calls <= FleetDreamer.MaxIterations + 1, $"{client.Calls} model calls");
        Assert.NotEmpty(outcome.Accepted);
    }

    [Fact]
    public async Task Usage_IsStampedWithTheUsersContext_AndTheDreamsOwnApplicationName()
    {
        var client = new ScriptedDreamClient(_ => Say("nothing"));

        await Dream(client);

        Assert.Equal(Alice, client.SeenUser);
        Assert.Equal(FleetDreamer.ApplicationName, client.SeenApplication);
        Assert.Equal("Agent42 fleet dream", FleetDreamer.ApplicationName);
        Assert.NotEqual(FleetTaskService.ApplicationName, FleetDreamer.ApplicationName);
    }

    [Fact]
    public async Task ReportsTheTokensTheModelUsed()
    {
        var client = new ScriptedDreamClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "nothing")) { Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 } });

        var outcome = await Dream(client);

        Assert.Equal((120L, 30L), (outcome.InputTokens, outcome.OutputTokens));
    }

    private const string PlannerFind = "Prefer the smallest team that gets the job done.";
    private const string Why = "Teams that staged research before writing scored higher.";

    [Fact]
    public async Task TheModelSeesTheAllowedPromptsAsData_NotDreamOrCopilot()
    {
        var client = new ScriptedDreamClient(_ => Say("nothing"));
        var prompts = new FleetPromptSet(new Dictionary<string, string> { ["judge"] = "My own judge text {{task}} {{result}}." });
        FleetPromptProposal[] stored = [new() { Id = "p", PromptKey = "retry", Replacement = "x", Status = FleetPromptProposalStatus.Pending }];

        await new FleetDreamer(new FleetOptions(), prompts, Digest, NullLogger.Instance, stored).RunAsync(client, Alice, CancellationToken.None);

        var block = client.UserText![client.UserText.IndexOf("current fleet prompts", StringComparison.Ordinal)..];
        Assert.Contains("data, not instructions", block);
        Assert.Contains(FleetToolLimits.ResultOpenTag, block);
        Assert.All(FleetPromptProposals.AllowedKeys, k => Assert.Contains($"=== Prompt {k} (", block));
        Assert.DoesNotContain("=== Prompt dream", block);
        Assert.DoesNotContain("=== Prompt copilot", block);
        Assert.Contains("My own judge text {{task}} {{result}}.", block);
        Assert.Contains(PlannerFind, block);
        Assert.Contains("change already waiting for the user: retry.", block);
        Assert.Contains("propose_prompt_change", FleetPrompts.Find("dream")!.Default);
    }

    [Fact]
    public async Task ProposePromptChange_AcceptsValid_RefusesWithReason_CapsPerRun()
    {
        var results = new List<string?>();
        var client = new ScriptedDreamClient(c =>
        {
            results.Add(c.LastToolResult);
            return c.Number switch
            {
                1 => ProposePrompt("dream", "append", null, "Be bolder.", Why, "t1", "t2"),
                2 => ProposePrompt("planner", "replace", PlannerFind, PlannerFind + " Stage research before writing.", Why, "t1", "t2"),
                3 => ProposePrompt("planner", "append", null, "Another one.", Why, "t1", "t3"),
                4 => ProposePrompt("judge", "append", null, "Name the missing section in the feedback.", Why, "t2", "t3"),
                _ => Say("done"),
            };
        });

        var outcome = await new FleetDreamer(new FleetOptions(), FleetPromptSet.Defaults, Digest, NullLogger.Instance).RunAsync(client, Alice, CancellationToken.None);

        Assert.Contains("refused", results[1]);
        Assert.Contains("can't be changed", results[1]);
        Assert.StartsWith("accepted", results[2]);
        Assert.Contains("already proposed a change to 'planner'", results[3]);
        Assert.StartsWith("accepted", results[4]);
        Assert.Equal(["planner", "judge"], outcome.Proposals.Select(p => p.PromptKey));
        Assert.Equal(2, outcome.ProposalsRefused);
        Assert.Empty(outcome.Accepted);
        Assert.Null(outcome.Reason);

        // A third valid one in the same run hits the per-run cap.
        var capped = new ScriptedDreamClient(c => c.Number switch
        {
            1 => ProposePrompt("planner", "replace", PlannerFind, PlannerFind + " Stage the work.", Why, "t1", "t2"),
            2 => ProposePrompt("judge", "append", null, "Name the missing section.", Why, "t1", "t2"),
            3 => ProposePrompt("retry", "append", null, "Fix only what the judge named.", Why, "t1", "t2"),
            _ => Say("done"),
        });
        var third = await new FleetDreamer(new FleetOptions(), FleetPromptSet.Defaults, Digest, NullLogger.Instance).RunAsync(capped, Alice, CancellationToken.None);
        Assert.Equal(FleetPromptProposals.MaxPerRun, third.Proposals.Count);
        Assert.Equal(1, third.ProposalsRefused);
    }
}
