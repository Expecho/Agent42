namespace Agent42.Tests;

/// <summary>
/// Guards the custom-conversation-group wiring (ADR 0073). Serves the real Backend <c>wwwroot</c>
/// through the shared <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings —
/// the same source-text heuristic as <see cref="PinChatAssetsTests"/>.
///
/// <para><b>What these cannot prove.</b> The repo has no JS runner by design, so every assertion here
/// shows the code <i>says</i> the right thing, never that a browser <i>does</i> it. "A filed chat
/// renders exactly once", the drag gesture itself, and whether <c>draggable</c> is re-read at drag
/// initiation are the manual DevHost pass recorded in ADR 0073.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class ChatGroupAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public ChatGroupAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // ADR 0073 (gotcha "Filing into a custom group", trap 4): drag-to-file is armed per INTERACTION from the pointer
    // type, never from a fine-pointer media query, which reports the PRIMARY pointer and so matches a touchscreen laptop,
    // where a live `draggable` races the 400 ms reveal. A plain substring search on purpose: it matches comments too, so
    // the rejected query is never spelled out in the script (the sidebar.js comment says so).
    [Fact]
    public async Task DragToFile_IsDecidedPerInteraction_NeverByAFinePointerQuery()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("li.draggable = e.pointerType === \"mouse\";", app);
        Assert.DoesNotContain("pointer: fine", app, StringComparison.Ordinal);
        Assert.DoesNotContain("pointer:fine", app, StringComparison.Ordinal);
    }

    // AC17: the agent walk skips BOTH pinned rows (ADR 0045) and rows filed into a custom group, or a
    // filed agent chat renders twice — once in its group and once under its agent.
    [Fact]
    public async Task AgentWalk_SkipsPinnedAndGroupedRows()
    {
        var app = await _fixture.GetAppScriptAsync();
        var walk = StaticAssetsServerFixture.Body(app, "function renderAgentWalk()");

        Assert.Contains("if (conv.pinned || (conv.groupId && knownGroupIds.has(conv.groupId))) continue;", walk);

        // The local Map is NOT called `groups`: renderSidebar and the passes read the module-level
        // `groups` import, and a function-scoped const of that name would shadow it for the whole body
        // and throw on first access (temporal dead zone) — a blank sidebar that no source-text guard in
        // this suite could see. Pinned here because it is invisible in every other way.
        Assert.Contains("const agentGroups = new Map()", walk);
        Assert.DoesNotContain("const groups = new Map()", walk);
    }

    // A custom group's rename and delete are hover-reveals, so on a phone they are opacity: 0 and
    // pointer-events: none until the ADR 0052 press-and-hold marks the group .is-revealed. Without a
    // gesture wired to the group they would be inert FOREVER there — the gap ADR 0045 explicitly refused
    // to ship for the conversation rows ("pinning impossible on the installed PWA while unpinning
    // worked"), one level up. The CSS for it is only reachable because buildChatGroup wires the reveal.
    [Fact]
    public async Task GroupActions_AreReachableOnTouch()
    {
        var app = await _fixture.GetAppScriptAsync();
        var group = StaticAssetsServerFixture.Body(app, "export function buildChatGroup(group, convs)");

        Assert.Contains("wireRowReveal(li, header, header)", group);

        // Listening on the HEADER, not the <li>, is the load-bearing half: the <li> contains the group's
        // conversation rows, and both timers fire at 400 ms with the outer listener running LAST — so an
        // <li>-scoped listener makes a long press on a row inside a group reveal the group instead.
        var reveal = StaticAssetsServerFixture.Body(app, "export function wireRowReveal(li, btn, listenOn = li)");
        Assert.Contains("listenOn.addEventListener(\"pointerdown\"", reveal);
        Assert.DoesNotContain("li.addEventListener(\"pointerdown\"", reveal);

        var css = await _client.GetStringAsync("/styles.css");
        var touchBlock = css[css.IndexOf("@media (hover: none)", StringComparison.Ordinal)..];
        Assert.Contains(".conv-group--custom.is-revealed .conv-group__action", touchBlock);
    }

}
