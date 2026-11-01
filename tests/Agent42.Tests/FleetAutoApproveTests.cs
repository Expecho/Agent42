using System.Runtime.CompilerServices;
using Agent42.Agents;
using Agent42.Backend;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // The SDK's permission RPC types are [Experimental]; these tests pin them.

namespace Agent42.Tests;

/// <summary>
/// Auto mode of a fleet task started from a chat (ADR 0115): every Copilot permission kind is approved once without
/// asking, the refusals outside the workspace still win, MCP elicitations are declined, and each automatic answer is
/// logged as a <c>question.auto</c> event. A page task's policy still asks.
/// </summary>
public sealed class FleetAutoApproveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-auto-" + Guid.NewGuid().ToString("N"));

    public FleetAutoApproveTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Policy_Auto_ApprovesShellWriteUrlMcpAndUnknown()
    {
        var interaction = new ScriptedFleetInteraction("n", "n", "n", "n", "n", "n", "n");
        var events = new RecordingFleetSink();
        var policy = new FleetCopilotPermissionPolicy(_root, interaction, CancellationToken.None, events);

        PermissionRequest[] requests =
        [
            Request<PermissionRequestShell>(),
            Write(Path.Combine(_root, "x.txt")),
            Request<PermissionRequestUrl>(),
            Request<PermissionRequestMcp>(),
            Request<PermissionRequestHook>(),
            Request<PermissionRequestMemory>(),
        ];
        foreach (var request in requests)
        {
            Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(request, null!));
        }

        Assert.Empty(interaction.Questions);
        var logged = events.Of("question.auto").ToList();
        Assert.Equal(requests.Length, logged.Count);
        Assert.All(logged, e => Assert.Equal("approved", e.GetProperty("answer").GetString()));
    }

    [Fact]
    public async Task Policy_Auto_StillRejectsOutsideWorkspace()
    {
        var events = new RecordingFleetSink();
        var policy = new FleetCopilotPermissionPolicy(_root, new ScriptedFleetInteraction(), CancellationToken.None, events);
        var outside = Path.GetFullPath(Path.Combine(_root, "..", "secret.txt"));

        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Write(outside), null!));
        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Read(outside), null!));
        Assert.Empty(events.Of("question.auto"));
    }

    [Fact]
    public async Task Policy_Manual_StillAsks()
    {
        var interaction = new ScriptedFleetInteraction("n");
        var policy = new FleetCopilotPermissionPolicy(_root, interaction, CancellationToken.None);

        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Request<PermissionRequestShell>(), null!));
        Assert.Single(interaction.Questions);
    }

    [Fact]
    public async Task Elicitation_Url_Declined()
    {
        var sink = new FleetElicitationSink(new DecliningFleetInteraction(new RecordingFleetSink()));

        var answer = await sink.AskAsync("One", "c1", new(McpElicitationLimits.UrlMode, "Sign in", Url: "https://login.example.com/x"), default);

        Assert.Equal(McpElicitationLimits.Decline, answer.Action);
    }

    [Fact]
    public async Task Elicitation_Form_Declined()
    {
        var sink = new FleetElicitationSink(new DecliningFleetInteraction(new RecordingFleetSink()));

        var answer = await sink.AskAsync("One", "c2", new(McpElicitationLimits.FormMode, "Your name?", "{\"type\":\"object\"}"), default);

        Assert.Equal(McpElicitationLimits.Decline, answer.Action);
        Assert.Null(answer.ContentJson);
    }

    [Fact]
    public async Task AutoAnswers_PublishQuestionAutoEvent()
    {
        var events = new RecordingFleetSink();

        var answer = await new DecliningFleetInteraction(events).AskAsync("  May I?  ", [new("y", "Yes")], "", default);

        Assert.Equal("n", answer);
        var logged = Assert.Single(events.Of("question.auto"));
        Assert.Equal("May I?", logged.GetProperty("question").GetString());
        Assert.Equal("declined", logged.GetProperty("answer").GetString());
        Assert.Empty(events.Of("prompt"));
    }

    private static PermissionRequestRead Read(string path) => With(Request<PermissionRequestRead>(), nameof(PermissionRequestRead.Path), path);

    private static PermissionRequestWrite Write(string file) => With(Request<PermissionRequestWrite>(), nameof(PermissionRequestWrite.FileName), file);

    // The SDK builds these from JSON; a test sets the one property the policy reads.
    private static T Request<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static T With<T>(T request, string property, object value)
    {
        typeof(T).GetProperty(property)!.SetValue(request, value);
        return request;
    }
}
