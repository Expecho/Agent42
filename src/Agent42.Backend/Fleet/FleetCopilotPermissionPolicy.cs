using System.Collections.Concurrent;
using GitHub.Copilot;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;

namespace Agent42.Backend;

// GHCP001: the SDK marks its permission RPC types [Experimental]; ADR 0097 accepts that, as 0085 did.
#pragma warning disable GHCP001

/// <summary>
/// Decides the permission requests of ONE fleet task's Copilot agents (ported from the engine's
/// <c>CopilotPermissionPolicy</c>, ADR 0097). Reads inside the task's working directory are allowed; reads and writes
/// outside it are refused. Agent42's own functions (<see cref="PermissionRequestCustomTool"/>) are approved without
/// asking, as ADR 0085's policy does, since default mode still raises them and asking per call would stall every
/// agent. A read-only MCP call is allowed. Everything else (writes, shell commands, URL fetches, other tools) is
/// asked to the user, with "always for this task". One instance per task, so "always" never leaks into another task.
/// <para>
/// <paramref name="autoApproveEvents"/> puts the policy in auto mode, for a task started from a chat (ADR 0115): every
/// request that would be asked is approved once instead, and logged as a <c>question.auto</c> event there. The
/// refusals outside the working directory still come first and are never approved.
/// </para>
/// </summary>
public sealed class FleetCopilotPermissionPolicy(
    string workingDirectory, IFleetInteraction interaction, CancellationToken cancellationToken, IFleetEventSink? autoApproveEvents = null)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));

    // Kinds of requests the user allowed for the rest of the task by answering 'always'.
    private readonly ConcurrentDictionary<string, bool> _allowedForTask = new();

    public async Task<PermissionDecision> HandleAsync(PermissionRequest request, PermissionInvocation invocation) => request switch
    {
        PermissionRequestCustomTool => PermissionDecision.ApproveOnce(),
        PermissionRequestRead { Path: { } path } when IsInWorkingDirectory(path) => PermissionDecision.ApproveOnce(),
        PermissionRequestRead { Path: var path } => PermissionDecision.Reject($"Reading {path} is not allowed, stay inside {_root}."),
        PermissionRequestWrite { FileName: var file } when file is null || !IsInWorkingDirectory(file) => PermissionDecision.Reject($"Writing {file} is not allowed, stay inside {_root}."),
        PermissionRequestWrite write => await AskAsync("write", $"write {write.FileName}", write.Intention),
        PermissionRequestShell shell => await AskAsync("shell", $"run '{shell.FullCommandText}'", shell.Intention),
        PermissionRequestUrl url => await AskAsync("url", $"fetch {url.Url}", url.Intention),
        PermissionRequestMcp { ReadOnly: true } => PermissionDecision.ApproveOnce(),
        PermissionRequestMcp mcp => await AskAsync($"mcp:{mcp.ServerName}", $"call {mcp.ServerName}/{mcp.ToolName}", null),
        _ => await AskAsync(request.Kind, $"use '{request.Kind}'", null),
    };

    private bool IsInWorkingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path, _root);
        return fullPath.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<PermissionDecision> AskAsync(string kind, string action, string? intention)
    {
        if (_allowedForTask.ContainsKey(kind))
        {
            return PermissionDecision.ApproveOnce();
        }

        var reason = string.IsNullOrWhiteSpace(intention) ? "" : $" ({intention})";
        if (autoApproveEvents is not null)
        {
            autoApproveEvents.Publish("question.auto", new { question = $"A Copilot agent wants to {action}{reason}.", answer = "approved" });
            return PermissionDecision.ApproveOnce();
        }

        var answer = await interaction.AskAsync(
            $"A Copilot agent wants to {action}{reason}. Allow it?",
            [new("y", "Allow"), new("a", "Always allow for this task"), new("n", "Deny")],
            "Or tell the agent why not",
            cancellationToken);
        switch (answer.Trim().ToLowerInvariant())
        {
            case "y" or "yes":
                return PermissionDecision.ApproveOnce();
            case "a" or "always":
                _allowedForTask[kind] = true;
                return PermissionDecision.ApproveOnce();
            case "" or "n" or "no":
                return PermissionDecision.Reject("The user denied this.");
            default:
                return PermissionDecision.Reject(answer.Trim());
        }
    }
}
