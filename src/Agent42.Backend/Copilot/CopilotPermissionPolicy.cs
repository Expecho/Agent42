using GitHub.Copilot;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;

namespace Agent42.Backend;

// GHCP001: the SDK marks its permission RPC types [Experimental]; the copilot agent kind accepts that (ADR 0085).
#pragma warning disable GHCP001

/// <summary>
/// Decides the permission requests of ONE copilot-kind chat session when built-in tools are on (ADR 0107). Nothing is
/// asked: a chat turn can run with nobody there (a reminder, a background turn), so the policy approves or refuses on
/// its own. Agent42's custom tools, the shell and URL fetches are approved. Built-in file reads and writes are approved
/// only inside the conversation's workspace, and refused (or, with no path, refused outright) everywhere else. Every
/// other kind (MCP, memory, hooks, extensions) is refused: no native MCP server is configured, and Agent42's MCP tools
/// arrive as custom tools. The shell cannot be path-checked (measured: its <c>ResolvedWorkingDirectory</c> is empty).
/// The shape of <see cref="FleetCopilotPermissionPolicy"/>, minus the asking.
/// </summary>
public sealed class CopilotPermissionPolicy(string workspace)
{
    private readonly string _root = CopilotWorkspacePaths.Root(workspace);

    public Task<PermissionDecision> HandleAsync(PermissionRequest request, PermissionInvocation invocation)
        => Task.FromResult(Decide(request));

    private PermissionDecision Decide(PermissionRequest request) => request switch
    {
        PermissionRequestCustomTool or PermissionRequestShell or PermissionRequestUrl => PermissionDecision.ApproveOnce(),
        PermissionRequestRead { Path: { } path } when IsInWorkspace(path) => PermissionDecision.ApproveOnce(),
        PermissionRequestRead { Path: var path } => PermissionDecision.Reject($"Reading {path} is not allowed, stay inside {_root}."),
        PermissionRequestWrite { FileName: { } file } when IsInWorkspace(file) => PermissionDecision.ApproveOnce(),
        PermissionRequestWrite { FileName: var file } => PermissionDecision.Reject($"Writing {file} is not allowed, stay inside {_root}."),
        _ => PermissionDecision.Reject($"'{request.Kind}' is not available in this chat."),
    };

    private bool IsInWorkspace(string path) => CopilotWorkspacePaths.IsInside(_root, path);
}
