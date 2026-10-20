using System.Runtime.CompilerServices;
using Agent42.Backend;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // The SDK's permission RPC types are [Experimental]; these tests pin them.

namespace Agent42.Tests;

/// <summary>
/// A copilot chat's permission policy with built-in tools on (ADR 0107): nothing is asked, because a chat turn can run
/// with nobody there. Custom tools, the shell and URL fetches are approved; file reads and writes only inside the
/// conversation's workspace; every other kind is refused.
/// </summary>
public sealed class CopilotPermissionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "copilot-policy-" + Guid.NewGuid().ToString("N"));

    public CopilotPermissionPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // AC5: the unattended kinds are approved outright.
    [Fact]
    public async Task Policy_ApprovesShellUrlCustom()
    {
        var policy = new CopilotPermissionPolicy(_root);

        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Request<PermissionRequestCustomTool>(), null!));
        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Request<PermissionRequestShell>(), null!));
        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Request<PermissionRequestUrl>(), null!));
    }

    // AC5: reads and writes inside the workspace are approved; outside it, through '..', in a sibling that shares the
    // prefix, or with no path at all, they are refused with the workspace named.
    [Fact]
    public async Task Policy_ReadWrite_InsideOnly()
    {
        var policy = new CopilotPermissionPolicy(_root);

        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Read(Path.Combine(_root, "a", "b.txt")), null!));
        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Read("relative/c.txt"), null!));
        Assert.IsType<PermissionDecisionApproveOnce>(await policy.HandleAsync(Write(Path.Combine(_root, "note.txt")), null!));

        foreach (var outside in new[] { Path.Combine(_root, "..", "secret.txt"), _root + "-sibling/x", "/etc/passwd" })
        {
            var read = Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Read(outside), null!));
            Assert.Contains(_root, read.Feedback);
            Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Write(outside), null!));
        }

        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Request<PermissionRequestRead>(), null!));
        Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(Request<PermissionRequestWrite>(), null!));
    }

    // AC5: no native MCP server, memory, hook or extension is configured, so each is refused, never asked.
    [Fact]
    public async Task Policy_RejectsOtherKinds()
    {
        var policy = new CopilotPermissionPolicy(_root);

        foreach (var request in new PermissionRequest[]
        {
            Request<PermissionRequestMcp>(), Request<PermissionRequestMemory>(), Request<PermissionRequestHook>(),
            Request<PermissionRequestExtensionManagement>(),
        })
        {
            Assert.IsType<PermissionDecisionReject>(await policy.HandleAsync(request, null!));
        }
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
