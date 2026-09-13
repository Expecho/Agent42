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

    // AC16: the three passes run in order — pinned block, then the user's custom groups, then the agent
    // walk. Order is asserted on the CALL SITES rather than on the passes' bodies, because the passes are
    // separate functions: renderSidebar measured exactly at the S3776 ceiling with two of them, so a
    // third had to be extracted or the build would fail.
    [Fact]
    public async Task RenderSidebar_CallsThePassesInOrder()
    {
        var app = await _fixture.GetAppScriptAsync();
        var render = StaticAssetsServerFixture.Body(app, "export function renderSidebar()");

        var pinnedAt = render.IndexOf("renderPinnedBlock()", StringComparison.Ordinal);
        var groupsAt = render.IndexOf("renderCustomGroups()", StringComparison.Ordinal);
        var walkAt = render.IndexOf("renderAgentWalk()", StringComparison.Ordinal);

        Assert.True(pinnedAt >= 0, "renderSidebar no longer emits the pinned block");
        Assert.True(groupsAt > pinnedAt, "custom groups must be emitted after the pinned block");
        Assert.True(walkAt > groupsAt, "the agent walk must be emitted after the custom groups");

        // What stays in renderSidebar's own body is load-bearing, not leftover: two OTHER test classes
        // assert against this same body, and extracting either line would make them vacuous rather than
        // failing. Pinned here so a future tidy-up cannot quietly move them into a pass.
        Assert.Contains("clearReveal();", render);
        Assert.Contains("conversationList.lastElementChild !== lastPinned", render);

        // A user with groups but no chats is not empty — their groups are on screen and droppable.
        Assert.Contains("conversations.length === 0 && groups.length === 0", render);
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

    // AC18: a conversation whose groupId names a group that no longer exists must fall through to the
    // agent walk and render ungrouped — never vanish from the sidebar. That is what testing membership
    // against the KNOWN ids buys over testing `conv.groupId` alone.
    [Fact]
    public async Task UnknownGroupId_FallsBackToTheUngroupedWalk()
    {
        var app = await _fixture.GetAppScriptAsync();
        var walk = StaticAssetsServerFixture.Body(app, "function renderAgentWalk()");

        Assert.Contains("const knownGroupIds = new Set(groups.map((g) => g.id))", walk);
        Assert.Contains("knownGroupIds.has(conv.groupId)", walk);
    }

    // AC19: a custom group WINS over the agent group, so an agent chat filed into one is rendered there
    // and loses the agent header that named it. It must carry the agent badge instead — for a continuous
    // agent (ADR 0016, exactly one conversation) that is the normal state, not an edge case.
    //
    // The disjunction is the point: one `hoisted` flag cannot carry two independent facts, and reverting
    // this to `hoisted && conv.agentId` silently strips the label from every filed agent chat.
    [Fact]
    public async Task GroupedAgentChat_CarriesTheAgentBadge()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
        Assert.Contains("(hoisted || grouped) && conv.agentId", row);

        // ...and the custom group is the call site that passes it.
        var group = StaticAssetsServerFixture.Body(app, "export function buildChatGroup(group, convs)");
        Assert.Contains("buildConvRow(conv, false, true)", group);
    }

    // AC20: the mirror image — a row in the pinned block sits outside its CUSTOM group too, so it needs a
    // group badge. Only when hoisted: a row rendered inside its group already has that header above it.
    [Fact]
    public async Task HoistedRow_CarriesTheGroupBadge()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
        Assert.Contains("hoisted && conv.groupId", row);
        Assert.Contains("conv__group", row);
        // Resolved against the group list, so a dangling filing renders no badge rather than "undefined".
        Assert.Contains("groups.find((g) => g.id === conv.groupId)", row);
    }

    // AC22: an empty group still renders and still reads as a drop target. Being able to exist with no
    // members is the entire reason groups are first-class entities rather than a name denormalized onto
    // each conversation — so this is the AC that would make that decision pointless if it regressed.
    [Fact]
    public async Task EmptyGroups_AreRenderedAndAreDropTargets()
    {
        var app = await _fixture.GetAppScriptAsync();

        // Pass 2 iterates the GROUP list, not the conversation list, which is what puts a memberless
        // group on screen at all.
        var pass = StaticAssetsServerFixture.Body(app, "function renderCustomGroups()");
        Assert.Contains("for (const group of groups)", pass);
        Assert.Contains("buildChatGroup(group, members)", pass);

        var group = StaticAssetsServerFixture.Body(app, "export function buildChatGroup(group, convs)");
        Assert.Contains("convs.length === 0", group);
        Assert.Contains("conv-group__empty", group);

        // The highlight is drawn on the whole group, not its header, so an empty one is visibly a target.
        var css = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".conv-group--custom.is-drop-target", css);
    }

    // AC23: the move action is on every row, at every width — the touch path for what a mouse does by
    // dragging, and the only way to CLEAR a filing (there is deliberately no drag-back-out drop region).
    [Fact]
    public async Task MoveAction_IsWiredIntoConvRow_AndOpensTheDialog()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
        Assert.Contains("conv__move", row);
        Assert.Contains("openMoveToGroup(conv.id", row);
        // Like its three siblings, the action must not also open the chat underneath it.
        Assert.Contains("e.stopPropagation();", row);

        var open = StaticAssetsServerFixture.Body(app, "export function openMoveToGroup(convId, convTitle, currentGroupId)");
        Assert.Contains("showModal()", open);
        // "No group" always leads, so clearing is one press away from any state.
        Assert.Contains("\"No group\"", open);
    }

    // AC24: drag-to-file is armed PER INTERACTION from the pointer type, never from a media query.
    //
    // A fine-pointer media query reports the PRIMARY pointer, so a touchscreen laptop matches it — and
    // that is exactly the device where a live `draggable` would put the browser's own long-press drag in a
    // race with the 400 ms reveal. The rule here is the same one wireRowReveal already uses, so the two
    // gestures can never disagree about what kind of press is happening.
    //
    // The forbidden query is spelled out ONCE below, in the assertion itself, and must not be quoted in
    // any comment in the shipped modules: this search runs over the whole concatenated script and does
    // not skip comments, so an explanatory mention would fail it (which is how it first failed).
    [Fact]
    public async Task RowDrag_IsEnabledOnlyForMousePointers()
    {
        var app = await _fixture.GetAppScriptAsync();

        var reveal = StaticAssetsServerFixture.Body(app, "function wireRowReveal(li, btn)");
        Assert.Contains("li.draggable = e.pointerType === \"mouse\"", reveal);

        var group = StaticAssetsServerFixture.Body(app, "export function buildChatGroup(group, convs)");
        Assert.Contains("li.draggable = e.pointerType === \"mouse\"", group);

        // The gate that must never appear: a media query would get the hybrid device wrong.
        Assert.DoesNotContain("pointer: fine", app);
    }

    // AC25: the drag listeners are delegated on the list, never wired per row. wireGroups() runs from
    // main.js before a single row exists, and renderSidebar destroys and rebuilds every row on every
    // call — so per-row listeners would attach to nothing and then be thrown away.
    [Fact]
    public async Task DragWiring_IsDelegatedOnTheList()
    {
        var app = await _fixture.GetAppScriptAsync();
        var wire = StaticAssetsServerFixture.Body(app, "function wireDragAndDrop()");

        Assert.Contains("conversationList.addEventListener(\"dragstart\"", wire);
        Assert.Contains("conversationList.addEventListener(\"dragover\"", wire);
        Assert.Contains("conversationList.addEventListener(\"drop\"", wire);
        // dragend fires whether the drop landed or the drag was abandoned, so the highlight is never
        // stranded on a group the user dragged away from.
        Assert.Contains("conversationList.addEventListener(\"dragend\"", wire);
        // Without preventDefault on dragover the element is not a drop target at all — the single
        // easiest way to ship drag-and-drop that silently does nothing.
        Assert.Contains("e.preventDefault()", wire);

        // The rows carry the ids the delegated handlers read, since they cannot close over `conv`.
        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
        Assert.Contains("li.dataset.convId = conv.id", row);
    }

    // AC26: the fourth glyph follows the ADR 0052 rule its three siblings follow — hidden AND INERT until
    // the row is revealed. opacity: 0 removes the paint, not the hit test, so a live-but-invisible control
    // on the right edge of every row is a thumb away from firing.
    [Fact]
    public async Task MoveAction_IsRevealedOnTouch_AndInertWhenUnrevealed()
    {
        var css = await _client.GetStringAsync("/styles.css");

        var touchBlock = css[css.IndexOf("@media (hover: none)", StringComparison.Ordinal)..];
        var inertAt = touchBlock.IndexOf("pointer-events: none", StringComparison.Ordinal);
        var revealAt = touchBlock.IndexOf(".conv-row.is-revealed .conv__move", StringComparison.Ordinal);

        Assert.True(inertAt >= 0, "the touch block no longer makes the row actions inert");
        Assert.True(revealAt >= 0, "the move action is not revealed by the long press");
        // In the inert list as well as the revealed one — both, or it is tappable while invisible.
        Assert.Contains(".conv__move,", touchBlock[..inertAt]);

        // And a hovering pointer still reveals it, from a gated block (an ungated :hover rule fires on
        // Android taps too and would light every row).
        Assert.Contains(".conv-row:hover .conv__move", css);
    }

    // AC27: the row reserves horizontal room for FOUR actions now. The glyphs are absolutely positioned
    // over the strip this padding creates, so a stale value overlaps them onto the title.
    [Fact]
    public async Task ConvPadding_ReservesRoomForFourActions()
    {
        var css = await _client.GetStringAsync("/styles.css");

        Assert.Contains("padding: 10px 106px 9px 14px", css);
        Assert.Contains("right: 79px", css); // .conv__move, left of pin/share/delete at 55/31/7
    }

    // AC28: deleting a group must confirm, and must say what it does NOT do — the × glyph is the same one
    // the conversation rows use for a genuinely destructive delete.
    [Fact]
    public async Task DeleteGroup_ConfirmsBeforeUngrouping()
    {
        var app = await _fixture.GetAppScriptAsync();
        var del = StaticAssetsServerFixture.Body(app, "async function deleteGroup(group)");

        Assert.Contains("confirm(", del);
        Assert.Contains("ungrouped", del);
        Assert.Contains("method: \"DELETE\"", del);
        // Both lists move: the group is gone AND its members are now ungrouped.
        Assert.Contains("refreshGroups();", del);
        Assert.Contains("refreshConversations();", del);
    }

    // AC29: groups are mirrored for offline (ADR 0032) and — the half that is easy to miss — seeded
    // BEFORE the first paint. renderSidebar partitions conversations by group id, so hydrating groups
    // after the sidebar render would show every filed chat in the recency walk and then visibly reflow.
    [Fact]
    public async Task Groups_AreMirroredForOffline_AndHydrateBeforeTheFirstRender()
    {
        var app = await _fixture.GetAppScriptAsync();

        var refresh = StaticAssetsServerFixture.Body(app, "export async function refreshGroups()");
        Assert.Contains("cachePut(\"groups\", groups)", refresh);
        Assert.Contains("cacheGet(\"groups\")", refresh);

        var hydrate = StaticAssetsServerFixture.Body(app, "async function hydrateFromCache()");
        var seedAt = hydrate.IndexOf("setGroups(cachedGroups)", StringComparison.Ordinal);
        var renderAt = hydrate.IndexOf("renderSidebar();", StringComparison.Ordinal);
        Assert.True(seedAt >= 0, "the offline cache no longer seeds the group list");
        Assert.True(renderAt > seedAt, "groups must be seeded before the sidebar's first paint");
    }

    // The group name is user-supplied text and reaches the DOM through textContent, never innerHTML —
    // it bypasses no sanitiser because it touches none. The row actions' SVG marks ARE innerHTML, but
    // those are author-controlled literals; a group name is not.
    [Fact]
    public async Task GroupNames_AreRenderedAsText()
    {
        var app = await _fixture.GetAppScriptAsync();

        var group = StaticAssetsServerFixture.Body(app, "export function buildChatGroup(group, convs)");
        Assert.Contains("name.textContent = group.name", group);
        Assert.DoesNotContain("innerHTML = group.name", group);

        var option = StaticAssetsServerFixture.Body(app, "function buildGroupOption(value, label, checked)");
        Assert.Contains("text.textContent = label", option);
    }
}
