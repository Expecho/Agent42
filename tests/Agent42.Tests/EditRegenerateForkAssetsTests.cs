namespace Agent42.Tests;

/// <summary>
/// Guards the edit / regenerate / fork wiring in the browser (ADR 0092). The same source-text heuristic as
/// <see cref="PinChatAssetsTests"/>: the repo has no JS runner by design, so these prove the code <i>says</i> the
/// right thing; the behaviour is covered by the grain and API tests plus the manual DevHost pass in ADR 0092.
/// </summary>
[Collection("StaticAssets")]
public sealed class EditRegenerateForkAssetsTests(StaticAssetsServerFixture fixture)
{
    // AC19: edit and regenerate re-read /messages, repaint from the SERVER's history, and send through the one
    // send() path with replaceLastTurn and both halves of the token.
    [Fact]
    public async Task EditRegenerate_ReRendersThenReplays()
    {
        var app = await fixture.GetAppScriptAsync();

        var regenerate = StaticAssetsServerFixture.Body(app, "async function regenerateLastReply()");
        Assert.Contains("fetchTurns(convId)", regenerate);
        Assert.Contains("repaint(convId, turns.slice(0, token.lastUserIndex))", regenerate);
        Assert.Contains("send(token.lastUser.text, replayOf(token))", regenerate);

        var edit = StaticAssetsServerFixture.Body(app, "async function editLastMessage()");
        Assert.Contains("fetchTurns(convId)", edit);
        Assert.Contains("openEditor(", edit);

        var replay = StaticAssetsServerFixture.Body(app, "function replayOf(token)");
        Assert.Contains("replaceLastTurn: true", replay);
        Assert.Contains("expectedUserTurnCount", replay);
        Assert.Contains("expectedLastUserTextHash", replay);

        Assert.Contains("crypto.subtle.digest(\"SHA-256\"", StaticAssetsServerFixture.Body(app, "async function sha256Hex(text)"));

        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt, replay = null)");
        Assert.Contains("replaceLastTurn: replay?.replaceLastTurn ?? false", send);
        Assert.Contains("expectedLastUserTextHash: replay?.expectedLastUserTextHash ?? null", send);
        // A refused replace must never be "recovered" into the old reply, and its text goes back to the composer.
        Assert.Contains("!(err.status && replay?.replaceLastTurn)", send);
        Assert.Contains("input.value = prompt", send);
    }

    // AC19: no actions while a turn streams, in a copilot chat or offline; no fork in incognito. Actions are
    // re-placed after every repaint and at both ends of a turn.
    [Fact]
    public async Task Actions_HiddenForCopilotStreamingAndIncognitoFork()
    {
        var app = await fixture.GetAppScriptAsync();

        var allowed = StaticAssetsServerFixture.Body(app, "function actionsAllowed()");
        Assert.Contains("!streaming", allowed);
        Assert.Contains("activeAgentKind !== \"copilot\"", allowed);
        Assert.Contains("navigator.onLine", allowed);

        var sync = StaticAssetsServerFixture.Body(app, "export function syncMessageActions()");
        Assert.Contains("if (!actionsAllowed()) return;", sync);
        Assert.Contains("if (!ephemeral) row.appendChild(actionButton(\"Fork from here\"", sync);

        Assert.Contains("syncMessageActions();", StaticAssetsServerFixture.Body(app, "export function renderTurns(id, turns)"));
        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt, replay = null)");
        Assert.True(CountOf(send, "syncMessageActions();") >= 2, "send() must clear the actions at the start and restore them at the end");
    }

    // AC19: fork trusts the DOM position only when the user counts agree, posts the fork route, and opens the new chat.
    [Fact]
    public async Task Fork_PostsTheForkRoute_AndOpensTheNewChat()
    {
        var app = await fixture.GetAppScriptAsync();

        var fork = StaticAssetsServerFixture.Body(app, "async function forkFrom(msg)");
        Assert.Contains("token.expectedUserTurnCount !== domUsers.length", fork);
        Assert.Contains("/fork`", fork);
        Assert.Contains("throughUserTurn,", fork);
        Assert.Contains("await refreshConversations();", fork);
        Assert.Contains("await selectConversation(conversationId);", fork);
    }

    private static int CountOf(string text, string part)
        => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
