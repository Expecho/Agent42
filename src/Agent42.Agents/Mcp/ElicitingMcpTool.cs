using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Wraps one tool of a connected MCP server so the server can ask the user for input during the call (ADR 0095).
/// Shaped like <see cref="UrlHarvestingMcpTool"/>: built in the grain, per conversation, because the Backend's tool
/// instances are shared by every conversation of the user. Around each call it opens a scope on the connection's
/// <see cref="McpElicitationRouter"/> holding this run's sink (from <paramref name="currentSink"/>, which reads the
/// run's broker; null for a headless run or a Copilot chat), the model's call id and the server's display name.
///
/// <para><b>-32042 (<c>UrlElicitationRequired</c>).</b> The server needs the user to visit a URL before the call can
/// work. Each URL is shown through the same sink, one at a time, and the model gets a short result that names the
/// <b>host only</b>. The model never sees an elicitation URL, so it cannot repeat or alter it.</para>
///
/// <para><b>It is the INNERMOST wrapper</b>, inside <see cref="UrlHarvestingMcpTool"/> and
/// <see cref="ReauthNoticeMcpTool"/>: the reauth notice stays outermost (ADR 0081), and the harvester sees only the
/// host-only text.</para>
/// </summary>
public sealed class ElicitingMcpTool(
    AIFunction inner,
    McpElicitationRouter router,
    string serverName,
    Func<IMcpElicitationSink?> currentSink) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var sink = currentSink();
        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? "";
        using var scope = router.Enter(sink, callId, serverName);
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (McpUrlElicitationRequiredException ex)
        {
            return await PresentUrlsAsync(ex.Elicitations, sink, callId, cancellationToken);
        }
    }

    private async Task<string> PresentUrlsAsync(
        IReadOnlyList<McpElicitationRequest> elicitations, IMcpElicitationSink? sink, string callId,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<string>();
        foreach (var request in elicitations)
        {
            if (!McpElicitationLimits.TryValidateUrl(request.Url, out var uri))
            {
                outcomes.Add("a link that was refused because it is not a safe https address");
                continue;
            }

            var answer = sink is null
                ? McpElicitationAnswer.Cancel
                : await sink.AskAsync(serverName, callId, request with { Mode = McpElicitationLimits.UrlMode }, cancellationToken);
            outcomes.Add($"{uri!.Host}: the user {Describe(answer.Action)}");
        }

        return $"The MCP server \"{serverName}\" needs the user to finish a step in their browser before this tool can "
            + $"run ({string.Join("; ", outcomes)}). Ask them to tell you when they are done, then try again. "
            + "Do not offer a link of your own.";
    }

    private static string Describe(string action) => action switch
    {
        McpElicitationLimits.Accept => "opened it",
        McpElicitationLimits.Decline => "declined",
        _ => "could not be asked or did not answer",
    };
}
