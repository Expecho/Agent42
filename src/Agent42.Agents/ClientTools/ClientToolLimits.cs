namespace Agent42.Agents;

/// <summary>
/// The client tools (ADR 0089): tools the model calls and the BROWSER runs. Their names, how long the grain waits
/// for an answer, and how much of an answer it keeps.
///
/// <para><b>The timeouts cap the WAIT, never the turn</b> (gotcha "Bounding a drained agent turn"): on timeout the
/// tool returns <see cref="TimeoutResult"/> as a normal result and the turn carries on and persists. The longest
/// one stays well under the Container Apps ingress request timeout (240 s, documented), so a confirm cannot by
/// itself outlive the request it is streamed on.</para>
/// </summary>
public static class ClientToolLimits
{
    public const string ConfirmAction = "confirm_action";

    public const string ControlArtifactPane = "control_artifact_pane";

    public const string GetBrowserContext = "get_browser_context";

    /// <summary>
    /// The user's position (ADR 0090). A client tool, but NOT a <c>browser_tools</c> one: it keeps its own
    /// <c>location</c> selectable key, so a loop over <see cref="ToolNames"/> must not assume that key.
    /// </summary>
    public const string GetCurrentLocation = "get_current_location";

    /// <summary>
    /// Every client tool name. The chat endpoint intersects the browser's advertised list with this, so a client
    /// can only narrow what it supports, never introduce a name.
    /// </summary>
    public static IReadOnlySet<string> ToolNames { get; } =
        new HashSet<string>(
            [ConfirmAction, ControlArtifactPane, GetBrowserContext, GetCurrentLocation], StringComparer.Ordinal);

    /// <summary>A human reads and answers <c>confirm_action</c>.</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The others are answered by code in the page.</summary>
    public static readonly TimeSpan AutomaticTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The most of a browser's answer the tool returns to the model. A tool's return value is persisted into the
    /// 2 MB session (ADR 0004, CLAUDE.md rule 5), so the server truncates whatever the browser sends.
    /// </summary>
    public const int MaxResultChars = 4000;

    /// <summary>The largest result body the endpoint accepts at all; anything bigger is a 413.</summary>
    public const int MaxResultRequestBytes = 8 * 1024;

    /// <summary>No browser can answer: a background run, or a client that did not advertise the tool.</summary>
    public const string UnavailableResult = """{"status":"unavailable"}""";

    /// <summary>The browser did not answer in time.</summary>
    public const string TimeoutResult = """{"status":"timeout"}""";
}
