using System.Collections.Concurrent;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>Records every fleet event as its type and JSON data, in order.</summary>
public sealed class RecordingFleetSink : IFleetEventSink
{
    private readonly ConcurrentQueue<(string Type, JsonElement Data)> _events = new();

    public IReadOnlyList<(string Type, JsonElement Data)> Events => [.. _events];

    public void Publish(string type, object data)
        => _events.Enqueue((type, JsonSerializer.SerializeToElement(data, FleetTeamPlan.JsonOptions)));

    public IEnumerable<JsonElement> Of(string type) => Events.Where(e => e.Type == type).Select(e => e.Data);
}

/// <summary>Answers fleet questions from a queue; an empty queue answers "y". Records every question asked.</summary>
public sealed class ScriptedFleetInteraction(params string[] answers) : IFleetInteraction
{
    private readonly ConcurrentQueue<string> _answers = new(answers);

    public ConcurrentQueue<string> Questions { get; } = new();

    public Task<string> AskAsync(string question, IReadOnlyList<FleetChoice> choices, string freeTextHint, CancellationToken cancellationToken)
    {
        Questions.Enqueue(question);
        return Task.FromResult(_answers.TryDequeue(out var answer) ? answer : "y");
    }
}

/// <summary>A fleet run over <see cref="FakeChatClient"/> with two models, a temp workspace and recording seams.</summary>
public sealed class FleetRunHarness : IDisposable
{
    public const string ModelA = "fake:model-a";
    public const string ModelB = "fake:model-b";

    public FleetRunHarness(FleetOptions? options = null, long maxTokens = 0, IReadOnlyList<AIFunction>? tools = null, params string[] answers)
    {
        Options = options ?? new FleetOptions { ApprovalDepth = -1 };
        Budget = new FleetBudget(maxTokens, Options.MaxAgentsPerTask, Options.BudgetSoftLimit, Options.MaxConcurrentSubTeamsPerLevel, Options.MaxTeamDepth);
        Workspace = new FleetWorkspace(Guid.NewGuid().ToString("N"));
        Canvas = new FleetCanvas(Store, "alice", Workspace.WorkspaceId, Events);
        Interaction = new ScriptedFleetInteraction(answers);
        var fake = new FakeChatClient();
        Run = new FleetRun
        {
            TaskId = "task-1",
            Options = Options,
            Prompts = FleetPromptSet.Defaults,
            Budget = Budget,
            Workspace = Workspace,
            Canvas = Canvas,
            DefaultModel = ModelA,
            Clients = new Dictionary<string, IChatClient>
            {
                [ModelA] = new FleetBudgetChatClient(fake, Budget),
                [ModelB] = new FleetBudgetChatClient(fake, Budget),
            },
            ModelNames = new Dictionary<string, string?> { [ModelA] = "Model A" },
            Tools = tools ?? [],
            Events = Events,
            Interaction = Interaction,
            Logger = NullLogger.Instance,
        };
        Runner = new FleetTeamRunner(Run);
    }

    public FleetOptions Options { get; }
    public FleetBudget Budget { get; }
    public FleetWorkspace Workspace { get; }
    public FleetStore Store { get; } = new(new InMemoryFleetBlobs());
    public FleetCanvas Canvas { get; }
    public RecordingFleetSink Events { get; } = new();
    public ScriptedFleetInteraction Interaction { get; }
    public FleetRun Run { get; }
    public FleetTeamRunner Runner { get; }

    public FleetTeamContext Root => new(0, Budget.Token);

    /// <summary><c>submit_team_plan</c> arguments for a team of the named agents.</summary>
    public static object Plan(string pattern, params string[] agents) => new
    {
        rationale = "fits",
        pattern,
        agents = agents.Select(a => new { name = a, yaml = FakeChatClient.FleetAgentYaml(a) }).ToArray(),
    };

    public void Dispose()
    {
        Workspace.Dispose();
        Canvas.Dispose();
        Budget.Dispose();
    }
}
