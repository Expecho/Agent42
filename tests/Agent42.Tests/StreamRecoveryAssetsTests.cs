namespace Agent42.Tests;

/// <summary>
/// Guards the recovery of a reply whose response stream died while the PWA was backgrounded. Serves the
/// real Backend <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on
/// the key source strings — the same source-text heuristic as <see cref="PinChatAssetsTests"/> (the repo
/// has no JS runner by design, so these prove the code <i>says</i> the right thing, never that a browser
/// <i>does</i> it).
///
/// <para>The server half of the same story — that the turn really does finish and persist after its
/// client disappears, which is the only reason there is anything to recover — is a runtime claim, and
/// is measured for real in <see cref="ChatStreamDisconnectTests"/>.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class StreamRecoveryAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;

    public StreamRecoveryAssetsTests(StaticAssetsServerFixture fixture) => _fixture = fixture;

    // A failed stream re-reads the conversation and adopts the persisted reply, rather than painting the
    // error over a turn that succeeded. The image loop matters as much as the text: the live `image`
    // event went down with the stream, so an image-generation turn (ADR 0020) would otherwise recover as
    // an empty bubble.
    [Fact]
    public async Task FailedStream_AdoptsThePersistedReplyFromTheHistory()
    {
        var app = await _fixture.GetAppScriptAsync();

        var recover = StaticAssetsServerFixture.Body(app, "async function recoverPersistedReply");
        Assert.Contains("/api/conversations/", recover);
        Assert.Contains("/messages", recover);
        Assert.Contains("setAgentMarkdown(agentMsg", recover);
        Assert.Contains("addAgentImage(agentMsg", recover);
        Assert.Contains("cachePut(\"messages:\"", recover); // the offline mirror (ADR 0032) stays in step
    }

    // An incognito chat is never written at all (ADR 0034), so there is nothing on the server to recover
    // and polling for it would hang the turn behind a caret that never stops. The guard is the first
    // thing in the function for exactly that reason.
    [Fact]
    public async Task Recovery_IsSkippedForAnEphemeralChat()
    {
        var app = await _fixture.GetAppScriptAsync();

        var recover = StaticAssetsServerFixture.Body(app, "async function recoverPersistedReply");
        Assert.Contains("if (ephemeral) return false;", recover);
    }

    // The backgrounded flag has to be latched by a listener: a frozen PWA runs no JS, so the stream's
    // rejection is not observed until the app is visible again — reading document.hidden in the catch
    // would always say "no" and every backgrounded turn would get the single-attempt treatment.
    [Fact]
    public async Task Send_LatchesVisibilityForTheLifeOfTheTurn()
    {
        var app = await _fixture.GetAppScriptAsync();

        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt)");
        Assert.Contains("addEventListener(\"visibilitychange\", onVisibilityChange)", send);
        Assert.Contains("removeEventListener(\"visibilitychange\", onVisibilityChange)", send);
        // …and the yardstick the recovered history is measured against is taken BEFORE this turn's two
        // bubbles are appended, or it would count them and never see the history grow.
        var prelude = send[..send.IndexOf("addMessage(\"user\")", StringComparison.Ordinal)];
        Assert.Contains("const priorTurns = thread.querySelectorAll(\".msg\").length;", prelude);
    }

    // Recovery is a SUCCESS: the reply is on the server, so the turn's tail must run too — the persona is
    // bound (pendingAgentId cleared, or the next turn rebinds) and the conversation is shareable
    // (ADR 0044's topbar button). Only the error branch is left to paint a failure.
    [Fact]
    public async Task RecoveredTurn_RunsTheSuccessTail()
    {
        var app = await _fixture.GetAppScriptAsync();

        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt)");
        var start = send.IndexOf("recoverPersistedReply(agentMsg", StringComparison.Ordinal);
        Assert.True(start >= 0, "send() no longer calls recoverPersistedReply");
        var branch = send[start..send.IndexOf("Transmission failed", StringComparison.Ordinal)];
        Assert.Contains("setPendingAgentId(null)", branch);
        Assert.Contains("setTopbarShare(convId", branch);
    }
}
