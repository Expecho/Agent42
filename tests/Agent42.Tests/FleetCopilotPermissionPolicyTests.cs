using System.Runtime.CompilerServices;
using Agent42.Backend;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // The SDK's permission RPC types are [Experimental]; these tests pin them.

namespace Agent42.Tests;

/// <summary>
/// The fleet's Copilot permission policy (ADR 0097, ported from the engine): reads inside the workspace are allowed,
/// anything outside is refused, Agent42's own tools are never asked, and writes, commands and fetches are asked with
/// an "always" that lasts for one task only.
/// </summary>
public sealed class FleetCopilotPermissionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-policy-" + Guid.NewGuid().ToString("N"));

    public FleetCopilotPermissionPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FleetCopilotPermissionPolicy Policy(ScriptedFleetInteraction interaction) => new(_root, interaction, CancellationToken.None);

    [Fact]
    public async Task CustomTool_ApprovedWithoutAsking()
    {
        var interaction = new ScriptedFleetInteraction("n");

        var decision = await Policy(interaction).HandleAsync(Request<PermissionRequestCustomTool>(), null!);

        Assert.IsType<PermissionDecisionApproveOnce>(decision);
        Assert.Empty(interaction.Questions);
    }

    [Fact]
    public async Task Read_InsideAllowed_OutsideRefused()
    {
        var interaction = new ScriptedFleetInteraction();
        var policy = Policy(interaction);

        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Read(Path.Combine(_root, "a", "b.txt")), null!));
        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Read("relative/c.txt"), null!));
        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Read(Path.Combine(_root, "..", "secret.txt")), null!));
        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Read(_root + "-sibling/x"), null!));
        Assert.Empty(interaction.Questions);
    }

    [Fact]
    public async Task Write_OutsideRefusedWithoutAsking()
    {
        var interaction = new ScriptedFleetInteraction("y");

        var decision = await Policy(interaction).HandleAsync(Write(Path.GetFullPath(Path.Combine(_root, "..", "x.txt"))), null!);

        Assert.IsType<PermissionDecisionReject>(decision);
        Assert.Empty(interaction.Questions);
    }

    [Fact]
    public async Task WriteShellUrl_Asked_AndAlwaysPersistsForTheTaskOnly()
    {
        var interaction = new ScriptedFleetInteraction("a", "y", "n");
        var policy = Policy(interaction);

        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Write(Path.Combine(_root, "x.txt")), null!));
        // "always" for writes: the next write is not asked.
        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Write(Path.Combine(_root, "y.txt")), null!));
        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Request<PermissionRequestShell>(), null!));
        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Request<PermissionRequestUrl>(), null!));
        Assert.Equal(3, interaction.Questions.Count);

        // A fresh task (a new policy) does not inherit "always".
        var next = new ScriptedFleetInteraction("n");
        Assert.IsType<PermissionDecisionReject>(await Policy(next).HandleAsync(Write(Path.Combine(_root, "z.txt")), null!));
        Assert.Single(next.Questions);
    }

    [Fact]
    public async Task FreeTextAnswer_BecomesRejectionReason()
    {
        var decision = await Policy(new ScriptedFleetInteraction("use the workspace instead")).HandleAsync(Request<PermissionRequestShell>(), null!);

        Assert.Equal("use the workspace instead", Assert.IsType<PermissionDecisionReject>(decision).Feedback);
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
