using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Client tools (ADR 0089): tools the model calls and the BROWSER runs. The call streams to the browser as an
/// ordinary <c>toolCallStart</c> flagged <c>clientTool</c>, the tool waits, the browser posts its result through
/// <see cref="SubmitClientToolResultAsync"/>, and the turn resumes. There are three: <c>confirm_action</c>
/// (the user approves or declines), <c>control_artifact_pane</c> (open, close or maximize the ADR 0084 pane) and
/// <c>get_browser_context</c> (facts only the browser knows).
///
/// <para><b>The broker is routed per RUN, never through a field the tool reads.</b> Two runs can be in flight on
/// one activation (a reminder or an <c>EnqueuePromptAsync</c> run in a gap of a streaming turn), and a field would
/// hand a background run's <c>confirm_action</c> to the user's browser as if their own turn had asked.
/// <see cref="CurrentClientToolBroker"/> is set around each of the interactive run's own pulls
/// (<see cref="PullAsync"/>), so only that run's tool invocations see it (measured, spike 0a in the plan).
/// <see cref="_clientToolBrokers"/> is the separate, by-identity registry the two interleaved calls look a call id
/// up in; nothing on the tool side reads it.</para>
///
/// <para><b>The wait is capped, never the turn.</b> On timeout the tool returns
/// <see cref="ClientToolLimits.TimeoutResult"/> and the turn completes and persists. Stop (ADR 0078) cancels the
/// wait through the tool's own token, which is the turn token, so a stopped wait takes the existing stopped-turn
/// path.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// The broker of the run whose pull is executing, or null. Set only by <see cref="PullAsync"/>; an async local
    /// rather than a field so that a background run pulled in a gap of the interactive one sees null.
    /// </summary>
    private static readonly AsyncLocal<ClientToolBroker?> CurrentClientToolBroker = new();

    /// <summary>
    /// The brokers of the interactive runs in flight on this activation, so the interleaved
    /// <see cref="SubmitClientToolResultAsync"/> / <see cref="GetPendingClientToolCallsAsync"/> can find a call.
    /// Added and removed by identity in <see cref="ChatAsync"/>, like <see cref="_turnCts"/>, and NOT part of the
    /// turn-scoped clear ritual: it names running turns, it does not carry a value.
    /// </summary>
    private readonly List<ClientToolBroker> _clientToolBrokers = [];

    private IEnumerable<AITool> ClientTools() =>
    [
        AIFunctionFactory.Create(
            ConfirmActionAsync,
            name: ClientToolLimits.ConfirmAction,
            description: "Ask the user to approve an action before you take it. Call this BEFORE anything "
                + "irreversible or outward-facing: sending or publishing something, deleting, paying, booking, or "
                + "changing data outside this chat. The user sees a dialog with your description and answers yes or "
                + "no. It returns {\"approved\":true} or {\"approved\":false}, or {\"status\":\"unavailable\"} when "
                + "nobody can answer (no browser is open on this chat) or {\"status\":\"timeout\"}. Take the action "
                + "ONLY on {\"approved\":true}; on anything else do not act, and tell the user what you would have "
                + "done. Do not use it for questions you can simply ask in your reply."),
        AIFunctionFactory.Create(
            ControlArtifactPaneAsync,
            name: ClientToolLimits.ControlArtifactPane,
            description: "Control the artifact side pane in the user's browser: open an artifact of this "
                + "conversation in it (optionally a specific version), close it, or switch it into or out of full "
                + "screen. Use it when showing the user an artifact helps, for example to show an earlier version "
                + "they ask about. Returns {\"ok\":true}, an error, or {\"status\":\"unavailable\"} when no browser "
                + "is open on this chat."),
        AIFunctionFactory.Create(
            GetBrowserContextAsync,
            name: ClientToolLimits.GetBrowserContext,
            description: "Get facts about the user's browser that you cannot otherwise know: their time zone, "
                + "locale and local time, light or dark color scheme, window size, whether the app is installed, "
                + "whether they are online, and the text they currently have selected on the page (if any). Use it "
                + "when the answer depends on one of these, for example to give a time in their time zone. Returns "
                + "{\"status\":\"unavailable\"} when no browser is open on this chat."),
    ];

    private Task<string> ConfirmActionAsync(
        [Description("What you are about to do, in one or two plain sentences the user can decide on, e.g. 'Send the email to Anna with the quarterly report attached.'")] string action,
        CancellationToken cancellationToken = default)
        => AwaitClientResultAsync(ClientToolLimits.ConfirmAction, cancellationToken);

    private Task<string> ControlArtifactPaneAsync(
        [Description("One of: open, close, fullscreen, exitFullscreen.")] string action,
        [Description("For open: the artifactId of an artifact in this conversation.")] string? artifactId = null,
        [Description("For open: the version to show; the latest when omitted.")] int? version = null,
        CancellationToken cancellationToken = default)
        => ValidatePaneRequest(action, artifactId, version) is { } problem
            ? Task.FromResult(problem)
            : AwaitClientResultAsync(ClientToolLimits.ControlArtifactPane, cancellationToken);

    private Task<string> GetBrowserContextAsync(CancellationToken cancellationToken = default)
        => AwaitClientResultAsync(ClientToolLimits.GetBrowserContext, cancellationToken);

    // Checked here, before any round trip, so an unknown artifact fails at once rather than after the browser's
    // own lookup — and the browser is never asked to open something this conversation does not own.
    private string? ValidatePaneRequest(string action, string? artifactId, int? version)
    {
        if (action is not ("open" or "close" or "fullscreen" or "exitFullscreen"))
        {
            return "action must be one of: open, close, fullscreen, exitFullscreen.";
        }

        if (action != "open")
        {
            return null;
        }

        if (!_state.State.Artifacts.TryGetValue(artifactId ?? "", out var record))
        {
            return UnknownArtifact(artifactId);
        }

        return version is { } wanted && record.Versions.TrueForAll(v => v.Version != wanted)
            ? $"Artifact {record.Id} has versions 1 to {record.Versions.Count}; there is no version {wanted}."
            : null;
    }

    // The wait every client tool shares. Unavailable at once when this run has no browser for the tool (a
    // background run, or a browser that did not advertise it); otherwise the call (announced already when its
    // toolCallStart was projected) waits for the browser's answer, bounded by the tool's timeout. A stop cancels
    // through `cancellationToken`, which rethrows into the stopped-turn path.
    private async Task<string> AwaitClientResultAsync(string toolName, CancellationToken cancellationToken)
    {
        var broker = CurrentClientToolBroker.Value;
        var call = FunctionInvokingChatClient.CurrentContext?.CallContent;
        if (broker is null || call is null || !broker.Supports(toolName))
        {
            return ClientToolLimits.UnavailableResult;
        }

        var answer = broker.WaitFor(call.CallId, toolName, SerializeOrNull(call.Arguments));
        try
        {
            return await answer.WaitAsync(_clientToolTimeouts.For(toolName), cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation(
                "Agent grain {GrainKey} timed out waiting for the browser to answer {Tool}",
                this.GetPrimaryKeyString(), toolName);
            return ClientToolLimits.TimeoutResult;
        }
        finally
        {
            broker.Forget(call.CallId);
        }
    }

    // One pull of a run's update stream, with the run's broker visible to whatever the pull invokes. The async
    // local is set in THIS method's own execution context, so it reaches the iterator body MoveNextAsync runs (and
    // its continuations, which capture that context) and nothing after this method returns.
    private static async ValueTask<bool> PullAsync(
        IAsyncEnumerator<AgentResponseUpdate> updates, ClientToolBroker? broker)
    {
        CurrentClientToolBroker.Value = broker;
        return await updates.MoveNextAsync();
    }

    // Flags a client tool's toolCallStart for the browser and announces the call to the broker, so an answer that
    // arrives before the tool is invoked is kept. Only on a run with a broker that supports the tool (elsewhere the
    // tool answers "unavailable" by itself), and never for a call the tool is about to refuse: the browser must not
    // act on a pane request the server rejects.
    private AgentStreamEvent MarkClientToolCall(AgentStreamEvent evt, ClientToolBroker? broker)
    {
        if (evt.Type != AgentStreamEventType.ToolCallStart || broker?.Supports(evt.ToolName) != true
            || evt.ToolCallId is not { } callId || RefusesBeforeAsking(evt.ToolName!, evt.ToolInput))
        {
            return evt;
        }

        broker.Announce(callId, evt.ToolName!, evt.ToolInput);
        return evt with { ClientTool = true };
    }

    // True when the tool will refuse these arguments without asking the browser. Only control_artifact_pane checks
    // anything; the arguments are read by their JSON names, a missing one as null, as the function binds them.
    private bool RefusesBeforeAsking(string toolName, string? input)
    {
        if (toolName != ClientToolLimits.ControlArtifactPane)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(input ?? "{}");
            var root = doc.RootElement;
            return ValidatePaneRequest(
                ReadString(root, "action") ?? "", ReadString(root, "artifactId"), ReadInt(root, "version")) is not null;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    // The broker of a new interactive turn, registered so the interleaved calls can reach it; null when the turn
    // has no browser that runs client tools.
    private ClientToolBroker? OpenClientToolBroker(bool interactive, IReadOnlyList<string>? clientTools)
    {
        if (!interactive || clientTools is not { Count: > 0 })
        {
            return null;
        }

        var broker = new ClientToolBroker(clientTools);
        _clientToolBrokers.Add(broker);
        return broker;
    }

    // Unregisters a turn's broker by identity and cancels whatever it still holds.
    private void CloseClientToolBroker(ClientToolBroker? broker)
    {
        if (broker is not null)
        {
            _clientToolBrokers.Remove(broker);
            broker.Close();
        }
    }

    public Task<bool> SubmitClientToolResultAsync(string toolCallId, string result)
        => Task.FromResult(_clientToolBrokers.Exists(b => b.TryComplete(toolCallId, result ?? "")));

    public Task<IReadOnlyList<PendingClientToolCall>> GetPendingClientToolCallsAsync()
        => Task.FromResult<IReadOnlyList<PendingClientToolCall>>(
            _clientToolBrokers.SelectMany(b => b.Pending).ToList());
}

/// <summary>
/// How long the grain waits for the browser to answer each kind of client tool (ADR 0089). An optional grain
/// dependency with a production default (<see cref="Default"/>), the <see cref="TimeProvider"/> arrangement: a
/// silo host that registers none gets the real limits, and the test silo registers short ones so the timeout path
/// is reachable without waiting two minutes.
/// </summary>
public sealed record ClientToolTimeouts(TimeSpan Confirm, TimeSpan Automatic)
{
    public static ClientToolTimeouts Default { get; } =
        new(ClientToolLimits.ConfirmTimeout, ClientToolLimits.AutomaticTimeout);

    public TimeSpan For(string toolName) => toolName == ClientToolLimits.ConfirmAction ? Confirm : Automatic;
}
