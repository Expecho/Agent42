namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of client tools and shared state (ADR 0089 AC5/AC17). The repo has no JS test runner,
/// so these assert on the source of the real <c>wwwroot</c> module graph (everything reachable from
/// <c>main.js</c>) and prove the wiring exists; the behaviour in a browser is checked by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class ClientToolsAssetsTests(StaticAssetsServerFixture fixture)
{
    // The request advertises the tools, and the stream reader hands a flagged call over WITHOUT awaiting it: an
    // awaited handler would stop the reader for as long as a dialog is open, so a stop or the turn's own events
    // could not arrive.
    [Fact]
    public async Task Send_AdvertisesClientTools_AndDispatchesFlaggedCallsWithoutBlocking()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("clientTools: CLIENT_TOOLS", app, StringComparison.Ordinal);
        var start = app.IndexOf("chunk.type === \"toolCallStart\"", StringComparison.Ordinal);
        var arm = CodeOnly(app[start..app.IndexOf("} else if", start, StringComparison.Ordinal)]);
        Assert.Contains("if (chunk.clientTool) runClientTool(convId, chunk);", arm, StringComparison.Ordinal);
        Assert.DoesNotContain("await runClientTool", arm, StringComparison.Ordinal);
    }

    // Every tool the server defines is one the page can run, and nothing else is advertised.
    [Fact]
    public async Task ClientToolList_MatchesTheServer()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains(
            "export const CLIENT_TOOLS = [\"confirm_action\", \"control_artifact_pane\", \"get_browser_context\"];",
            app, StringComparison.Ordinal);
        Assert.Equal(3, Agent42.Agents.ClientToolLimits.ToolNames.Count);
    }

    // AC5: stream recovery answers the calls the server is still waiting on (a confirm a frozen PWA never showed).
    [Fact]
    public async Task Recovery_ResumesPendingClientTools()
    {
        var app = await fixture.GetAppScriptAsync();

        var recover = CodeOnly(StaticAssetsServerFixture.Body(app, "async function recoverPersistedReply"));
        Assert.Contains("resumePendingClientTools(convId)", recover, StringComparison.Ordinal);
        Assert.Contains("/pending-tool-calls", app, StringComparison.Ordinal);
    }

    // The action text is model output and the state is model output and user text: the only safe sinks are
    // textContent and value, in both modules.
    [Theory]
    [InlineData("export async function runClientTool")]
    [InlineData("function confirmAction")]
    [InlineData("function render()")]
    [InlineData("function renderChecklist")]
    public async Task ModelText_IsNeverWrittenAsMarkup(string declaration)
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.DoesNotContain("innerHTML", CodeOnly(StaticAssetsServerFixture.Body(app, declaration)), StringComparison.Ordinal);
    }

    // Esc closes a <dialog> WITHOUT setting returnValue, so a stale "approve" from the previous request would make
    // Esc approve. The reset before showModal is what makes Esc a decline.
    [Fact]
    public async Task ConfirmDialog_ResetsItsAnswerBeforeEachRequest()
    {
        var app = await fixture.GetAppScriptAsync();

        var confirm = CodeOnly(StaticAssetsServerFixture.Body(app, "function confirmAction"));
        var reset = confirm.IndexOf("confirmActionDialog.returnValue = \"\"", StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < confirm.IndexOf("showModal()", StringComparison.Ordinal),
            "returnValue must be reset before the dialog opens");
        Assert.Contains("returnValue === \"approve\"", app, StringComparison.Ordinal);
    }

    // Shared state: the live snapshot arm, the load on open and the clear on every switch.
    [Fact]
    public async Task SharedState_IsFedByTheStreamAndFollowsTheOpenChat()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("chunk.type === \"stateSnapshot\"", app, StringComparison.Ordinal);
        Assert.Contains("applyStateSnapshot(convId, chunk.state, chunk.stateVersion)", app, StringComparison.Ordinal);
        Assert.Contains("loadSharedState(id);", CodeOnly(StaticAssetsServerFixture.Body(app, "export async function selectConversation")), StringComparison.Ordinal);
        Assert.Contains("clearSharedState();", CodeOnly(StaticAssetsServerFixture.Body(app, "export function clearThread")), StringComparison.Ordinal);
        Assert.Contains("baseVersion: version", app, StringComparison.Ordinal);
    }

    // The service worker lists both modules (SHELL is all-or-nothing) and moved its cache version on.
    [Fact]
    public async Task ServiceWorker_ListsBothModules()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/clienttools.js\"", sw, StringComparison.Ordinal);
        Assert.Contains("\"/js/sharedstate.js\"", sw, StringComparison.Ordinal);
        Assert.Contains("agent42-shell-v54", sw, StringComparison.Ordinal);
    }

    private static string CodeOnly(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));
}
