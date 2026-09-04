using System.Text.Json;

namespace Agent42.Tests;

/// <summary>
/// Serves the real Backend <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/>
/// (same <c>UseDefaultFiles()</c> / <c>UseStaticFiles()</c> pair as Program.cs), so the PWA
/// installability contract the browser depends on (manifest MIME + fields, service worker, icons)
/// is exercised end to end. Production/DevHost serve the same physical files via the SDK
/// static-web-assets provider; both resolve MIME through the default
/// FileExtensionContentTypeProvider, so the content-type contract is equivalent.
/// </summary>
[Collection("StaticAssets")]
public sealed class PwaAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public PwaAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Manifest_IsServed_WithInstallableFields()
    {
        var response = await _client.GetAsync("/manifest.webmanifest");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());

        var sizes = root.GetProperty("icons").EnumerateArray()
            .Where(i => i.GetProperty("type").GetString() == "image/png")
            .Select(i => i.GetProperty("sizes").GetString())
            .ToList();
        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
    }

    [Fact]
    public async Task ServiceWorker_IsServed_AsJavaScript()
    {
        var response = await _client.GetAsync("/sw.js");

        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.Contains(mediaType, new[] { "text/javascript", "application/javascript" });
    }

    // ADR 0041 — the app's entry point is reachable. Since the split, every source-text guard enters the
    // module graph at /js/main.js directly, so a broken, missing or type-less <script> tag in index.html
    // would leave the whole suite green while the app is dead in a browser. Before the split that was
    // guarded only implicitly, by every asset test doing GET /app.js.
    [Fact]
    public async Task EntryPointModule_IsServed_AsJavaScript()
    {
        var response = await _client.GetAsync("/js/main.js");

        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.Contains(mediaType, new[] { "text/javascript", "application/javascript" });

        // …and index.html actually loads it, as a module (a classic script would break every import).
        var html = await _client.GetStringAsync("/index.html");
        Assert.Contains("<script type=\"module\" src=\"js/main.js\"></script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceWorker_BypassesApiBeforeCaching()
    {
        // Guards the ADR-0002 auth invariant: the /api bypass must sit before any caching
        // so auth-bearing, streaming API responses are never cached. Source-text heuristic
        // (the repo has no JS test runner by design) — proves position, not logic.
        var response = await _client.GetAsync("/sw.js");
        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStringAsync();

        var apiGuard = source.IndexOf("startsWith(\"/api\")", StringComparison.Ordinal);
        Assert.True(apiGuard >= 0, "Service worker is missing the /api bypass guard");

        // event.respondWith( is the interception mechanism and appears only in the fetch handler,
        // so the /api early-return preceding every respondWith is the invariant we want:
        // /api requests are never intercepted or cached. Anchor on the call form (not the bare word
        // "respondWith", which also occurs in the /api-guard comment) so the position is the real one.
        var firstRespond = source.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(firstRespond >= 0, "Service worker has no fetch handler (respondWith)");
        Assert.True(apiGuard < firstRespond, "The /api bypass must precede any respondWith");
    }

    [Fact]
    public async Task ServiceWorker_BypassesTheProxiedSearchUiBeforeCaching()
    {
        // Same invariant as the /api guard, for the SearXNG UI proxy (ADR 0059) - the first non-/api route
        // in MapAgentApi. Its subresources carry the Easy Auth cookie and per-user SearXNG preference
        // cookies, so caching them in the SHARED shell cache would leak one user's state to the next; and
        // the navigate branch's offline fallback would paint /index.html - the chat app - in place of a
        // /searxng/ page. Source-text heuristic, like the /api guard above: proves position, not logic.
        var response = await _client.GetAsync("/sw.js");
        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStringAsync();

        var searxngGuard = source.IndexOf("startsWith(\"/searxng\")", StringComparison.Ordinal);
        Assert.True(searxngGuard >= 0, "Service worker is missing the /searxng bypass guard");

        var firstRespond = source.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(searxngGuard < firstRespond, "The /searxng bypass must precede any respondWith");
    }

    /// <summary>
    /// AC 8. The same invariant again for the Orleans dashboard (ADR 0062). It is rendered by the
    /// package, not part of the cached shell, and its bundle is 3.5 MB. The half that matters most is
    /// the navigate branch: the owner gate answers a non-owner with 404, and the offline fallback would
    /// paint <c>/index.html</c> — the chat app — in its place, a cached shell quietly contradicting the
    /// gate. Source-text heuristic, like the two guards above: proves position, not logic.
    /// </summary>
    [Fact]
    public async Task ServiceWorker_BypassesTheDashboardBeforeCaching()
    {
        var response = await _client.GetAsync("/sw.js");
        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStringAsync();

        var dashboardGuard = source.IndexOf("startsWith(\"/dashboard\")", StringComparison.Ordinal);
        Assert.True(dashboardGuard >= 0, "Service worker is missing the /dashboard bypass guard");

        var firstRespond = source.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(dashboardGuard < firstRespond, "The /dashboard bypass must precede any respondWith");
    }

    [Theory]
    [InlineData("/icons/icon-192.png")]
    [InlineData("/icons/icon-512.png")]
    [InlineData("/icons/icon-maskable-512.png")]
    public async Task Icons_AreServed_AsPng(string path)
    {
        var response = await _client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Frontend_WarnsWhenUploadedDocumentYieldedNoText()
    {
        // A document upload with chunkCount 0 (e.g. a scanned PDF) is NOT registered with the grain
        // server-side, so the assistant stays unaware of it. The frontend must therefore flag it
        // instead of drawing a normal "just indexed" chip that invites an unanswerable question.
        // Source-text heuristic (the repo has no JS test runner by design) — proves the warning
        // branch and its style hook exist, not their runtime behaviour.
        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("chunkCount > 0", app);
        Assert.Contains("attach-chip--warn", app);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".attach-chip--warn", styles);
    }

    [Fact]
    public async Task Frontend_GroupsAgentConversationsCollapsibly()
    {
        // Agent-produced chats are grouped under a collapsible header keyed by conv.agentId, with
        // expand state persisted to localStorage (groups start collapsed). Source-text heuristic (no
        // JS test runner by design) — proves the grouping code and its style hooks shipped, not their runtime behaviour.
        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("buildAgentGroup", app);
        Assert.Contains("conv.agentId", app);
        Assert.Contains("EXPANDED_AGENTS_KEY", app);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".conv-group", styles);
        Assert.Contains(".conv-group.is-collapsed .conv-group__body", styles);
    }

    [Fact]
    public async Task Frontend_GuardsUploadResultAgainstMidUploadConversationSwitch()
    {
        // Mode B: an upload is keyed to the conversation it started in, but its result lands
        // whenever the await resolves — a mid-upload switch would inject the chip into the wrong
        // conversation. handleFiles must pin uploadId at entry and bail when activeId moves on.
        // Source-text heuristic (no JS test runner by design) — proves the guard exists.
        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("uploadId", app);
        Assert.Contains("activeId !== uploadId", app);
    }

    [Fact]
    public async Task Frontend_SurfacesFailedUploadAsErrorChip()
    {
        // Mode A: a non-OK upload used to be silently dropped (if (!res.ok) continue;). It must now
        // render a dismissible error chip so the user knows the file did not attach. Source-text
        // heuristic (no JS test runner by design) — asserts the observable render, not the specific
        // body-read call, and that the bare swallow is gone.
        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("attach-chip--error", app);
        Assert.DoesNotContain("if (!res.ok) continue;", app);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".attach-chip--error", styles);
    }

    [Fact]
    public async Task IndexHtml_LinksManifest_AndAppRegistersServiceWorker()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Contains("rel=\"manifest\"", index);
        Assert.Contains("theme-color", index);

        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("serviceWorker.register", app);
    }

    // Theme feature (light/dark/system) invariants that silently regress with no JS test runner
    // (source-text heuristic, like the failed-upload chip test above). Behavior — live preview,
    // revert-on-cancel, OS-follow — is verified by driving the DevHost UI, not asserted here.
    [Fact]
    public async Task Frontend_ShipsThemeSelector_NoFoucScript_AndLightPalette()
    {
        var index = await _client.GetStringAsync("/");
        // The theme <select> and the pre-paint no-FOUC head script (reads the localStorage cache
        // and sets data-theme before js/main.js). Both must exist or the picker/anti-flash break.
        Assert.Contains("id=\"profileTheme\"", index);
        Assert.Contains("agent42.theme", index);
        Assert.Contains("data-theme", index);

        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("THEME_KEY", app);
        Assert.Contains("applyTheme", app);
        // The staged commit sends theme in the profile PUT payload; the OS-follow listener backs "system".
        Assert.Contains("theme: profileTheme.value", app);
        Assert.Contains("prefers-color-scheme: dark", app);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains("[data-theme=\"light\"]", styles);
    }

    [Fact]
    public async Task ServiceWorker_CacheNameIsCurrent()
    {
        // Bumped whenever the cached shell (index/styles/the js/ module graph) changes, so returning
        // users don't get stale assets — sw.js serves the shell cache-first with background revalidate.
        //
        // Since ADR 0041 the bump is no longer merely hygienic: subresources are served cache-first, so
        // the window between a network-first index.html and the new SW activating can serve a version-
        // SKEWED module graph, and a renamed export then throws "does not provide an export named X" —
        // a blank app, not a degraded one. Every MR touching wwwroot/js/ must bump this.
        var sw = await _client.GetStringAsync("/sw.js");
        Assert.Contains("agent42-shell-v31", sw);
    }

    // Offline read cache (ADR 0032): agents, conversations and previously-opened chat histories are
    // mirrored into an IndexedDB "agent42-cache" store in js/cache.js so the installed PWA can render the
    // user's data offline. This lives entirely in the app modules — the service worker still never touches /api
    // (ADR 0002), so ServiceWorker_BypassesApiBeforeCaching stays the guard for that invariant.
    // Source-text heuristic (no JS runner by design) — proves the wiring shipped, not runtime behaviour.
    [Fact]
    public async Task Frontend_MirrorsAgentsAndChatsForOffline()
    {
        var app = await _fixture.GetAppScriptAsync();

        // The dedicated read-cache store + its best-effort helpers.
        Assert.Contains("agent42-cache", app);
        Assert.Contains("function cacheGet", app);
        Assert.Contains("function cachePut", app);

        // Startup paints the cached lists before/instead of the network fetch resolving.
        Assert.Contains("function hydrateFromCache", app);
        Assert.Contains("hydrateFromCache()", app);

        // The two list loaders persist on success and fall back to the cache on failure.
        Assert.Contains("cachePut(\"conversations\", conversations)", app);
        Assert.Contains("cachePut(\"agents\", agents)", app);
        Assert.Contains("cacheGet(\"conversations\")", app);
        Assert.Contains("cacheGet(\"agents\")", app);

        // A previously-opened chat's history is mirrored per id and re-rendered from the cache offline,
        // through the same renderTurns() the live path uses.
        Assert.Contains("cachePut(\"messages:\" + id, turns)", app);
        Assert.Contains("cacheGet(\"messages:\" + id)", app);
        Assert.Contains("function renderTurns", app);
    }

    // Follow-up suggestion chips: the NDJSON "suggestions" event renders up to 3 clickable chips under the
    // reply, clicking one sends it, and a Preferences toggle (default ON) controls the feature. Source-text
    // heuristic (no JS runner by design); behaviour is verified by driving DevHost.
    [Fact]
    public async Task Frontend_ShipsFollowUpSuggestions()
    {
        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("chunk.type === \"suggestions\"", app);
        Assert.Contains("function renderSuggestions", app);
        Assert.Contains("function clearSuggestions", app);
        Assert.Contains("suggestFollowUps", app); // load + save the preference

        var indexHtml = await _client.GetStringAsync("/index.html");
        Assert.Contains("id=\"profileSuggest\"", indexHtml);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".suggestion-chip", styles);
    }

    // Copy-to-clipboard button on finalized agent replies: a hover/focus-revealed glyph button at the
    // bottom of the bubble that puts the reply's raw markdown on the clipboard (source-text heuristic;
    // the repo has no JS test runner by design). Behaviour — actual copy, glyph swap, hover reveal — is
    // verified by driving DevHost, not asserted here. Assertions are mutation-observable.
    [Fact]
    public async Task Frontend_ShipsCopyResponseButton()
    {
        var styles = await _client.GetStringAsync("/styles.css");
        // AC1: the reveal rule keys off .msg--agent hover / the button's focus, and the base rule
        // hides the button at rest (opacity: 0 anchored INSIDE the .msg__copy block, not anywhere).
        Assert.Contains(".msg--agent:hover .msg__copy", styles);
        Assert.Matches(@"\.msg__copy\s*\{[^}]*opacity:\s*0", styles);

        var app = await _fixture.GetAppScriptAsync();
        // AC2: the helper writes the raw text to the clipboard and swaps the glyph to a check on success.
        Assert.Contains("msg__copy", app);
        Assert.Contains("navigator.clipboard.writeText", app);
        Assert.Contains("✓", app); // ✓ confirmation glyph swap

        // AC3: the helper is wired on BOTH finalize paths specifically, not merely referenced twice on
        // one. The streaming site passes the raw `answer` accumulator; the reload site passes `turn.text`.
        // Anchoring on the distinct argument proves the path, not a bare ≥2× count (critic M3).
        Assert.Contains("addCopyButton(agentMsg, answer)", app);
        Assert.Contains("addCopyButton(msg, turn.text)", app);
    }

    // Web Share Target for the Android PWA (ADR 0023): an installed app appears in the system share
    // sheet and receives text/url/files. The three assets below carry the contract; behaviour (the
    // destination picker, prefill, attach, review-then-send) is verified by driving DevHost, not here.

    [Fact]
    public async Task Manifest_DeclaresShareTarget()
    {
        // AC1: the manifest must declare a POST/multipart share_target at /share accepting text/url and
        // files including images + PDFs, so Android offers Agent 42 as a share destination.
        var manifest = await _client.GetStringAsync("/manifest.webmanifest");
        using var doc = JsonDocument.Parse(manifest);
        var share = doc.RootElement.GetProperty("share_target");

        Assert.Equal("/share", share.GetProperty("action").GetString());
        Assert.Equal("POST", share.GetProperty("method").GetString());
        Assert.Equal("multipart/form-data", share.GetProperty("enctype").GetString());

        var accept = share.GetProperty("params").GetProperty("files")[0]
            .GetProperty("accept").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("image/*", accept);
        Assert.Contains("application/pdf", accept);
    }

    [Fact]
    public async Task ServiceWorker_HandlesShareTarget()
    {
        // AC2: sw.js intercepts POST /share client-side (so the body never hits the network / Easy Auth,
        // ADR 0002/0023). The new respondWith this introduces must sit AFTER the /api bypass, or the
        // ADR-0002 invariant regresses. Source-text heuristic (no JS runner) — proves position, not logic.
        var sw = await _client.GetStringAsync("/sw.js");

        Assert.Contains("\"/share\"", sw);
        Assert.Contains("handleShare", sw);

        var apiGuard = sw.IndexOf("startsWith(\"/api\")", StringComparison.Ordinal);
        // Anchor on the call form event.respondWith( — the bare word "respondWith" also occurs in the
        // /api-guard comment, so measuring it would assert a comment position, not the real branch.
        var firstRespond = sw.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(apiGuard >= 0, "Service worker is missing the /api bypass guard");
        Assert.True(firstRespond >= 0, "Service worker has no fetch handler (respondWith)");
        Assert.True(apiGuard < firstRespond,
            "The /api bypass must precede any respondWith, including the new share branch");
    }

    // AC10: sw.js registers the two Web Push handlers (ADR 0028) — `push` (always shows a notification,
    // honouring Chromium's userVisibleOnly contract) and `notificationclick` (focus/open the conversation).
    // Source-text heuristic (no JS runner) — proves the listeners exist; behaviour is verified on a device.
    [Fact]
    public async Task ServiceWorker_HandlesPush()
    {
        var sw = await _client.GetStringAsync("/sw.js");

        Assert.Contains("addEventListener(\"push\"", sw);
        Assert.Contains("addEventListener(\"notificationclick\"", sw);
        Assert.Contains("showNotification", sw);
        // The click handler tells js/push.js which conversation to open (its message handler → selectConversation).
        Assert.Contains("open-conversation", sw);
    }

    // ADR 0028: the Preferences → General push toggle markup + the js/push.js subscribe/config wiring. Presence
    // guard only (no JS runner) — the interactive subscribe flow is verified manually on a device.
    [Fact]
    public async Task Frontend_ShipsPushToggle()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Contains("id=\"pushSection\"", index);
        Assert.Contains("id=\"pushToggleBtn\"", index);

        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("/api/push/config", app);
        Assert.Contains("pushManager.subscribe", app);
        Assert.Contains("open-conversation", app);
    }

    [Fact]
    public async Task Frontend_ShipsShareDestinationPicker()
    {
        // AC4: the shell carries the per-share destination picker dialog + its agent list container.
        var index = await _client.GetStringAsync("/");
        Assert.Contains("id=\"shareDialog\"", index);
        Assert.Contains("id=\"shareAgentList\"", index);
    }

    [Fact]
    public async Task Frontend_WiresShareHandoffToHandleFiles()
    {
        // AC5 — structural/weak check (Critic): handleFiles pervades the app modules, so this proves the share
        // hand-off strings exist AND that applyShare routes the shared files through handleFiles
        // (position-bounded, mutation-observable). The real proof is the manual DevHost drive (AC6).
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("has(\"share\")", app);   // the /?share=1 load-time hand-off detection
        Assert.Contains("readSharePayload", app); // reads the IndexedDB stash the SW wrote

        // handleFiles must be called INSIDE applyShare, not merely present somewhere in the file.
        var applyShare = app.IndexOf("function applyShare", StringComparison.Ordinal);
        Assert.True(applyShare >= 0, "js/sharetarget.js is missing the applyShare share handler");
        var boundary = app.IndexOf("function initShare", applyShare, StringComparison.Ordinal);
        if (boundary < 0) boundary = app.Length;
        var handleFilesInApply = app.IndexOf("handleFiles(", applyShare, StringComparison.Ordinal);
        Assert.True(handleFilesInApply >= 0 && handleFilesInApply < boundary,
            "applyShare must route the shared files through handleFiles");
    }

    // The per-conversation tools control (ADR 0021) is an icon-only button inside the composer frame,
    // next to the attach icon (source-text heuristic; the repo has no JS test runner by design).
    // Assertions are mutation-observable — index.html already contains other aria-labels and a "// tools"
    // agent-form label, so whole-file Contains checks would pass vacuously; these prove position + form.
    [Fact]
    public async Task Frontend_ToolsControlIsIconButtonInComposerFrame()
    {
        var index = await _client.GetStringAsync("/");

        // Accessible name co-occurs on the target button's tag (icon-only ⇒ needs an aria-label).
        Assert.Matches(@"class=""composer__tools""[^>]*aria-label", index);

        // The tools button sits between the attach icon and the textarea — i.e. inside .composer__frame.
        var tools = index.IndexOf("composer__tools", StringComparison.Ordinal);
        var attach = index.IndexOf("attachBtn", StringComparison.Ordinal);
        var input = index.IndexOf("id=\"promptInput\"", StringComparison.Ordinal);
        Assert.True(attach >= 0 && tools >= 0 && input >= 0, "composer frame elements missing");
        Assert.True(tools > attach, "tools button must follow the attach icon");
        Assert.True(tools < input, "tools button must precede the prompt textarea (i.e. be in the frame)");

        // The old text button is gone (icon-only conversion): target its removed class, not the
        // surviving "// tools" agent-form label at index.html:~210.
        Assert.DoesNotContain("model__tools", index);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".composer__tools", styles);
    }

    // The Preferences dialog is split into General + Tools + Model providers tabs (source-text
    // heuristic). Tab switching / keyboard nav is verified by driving the DevHost UI, not asserted here.
    [Fact]
    public async Task Preferences_HasGeneralToolsAndProvidersTabs()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("role=\"tablist\"", index);
        Assert.Contains("id=\"tabGeneral\"", index);
        Assert.Contains("id=\"tabTools\"", index);
        Assert.Contains("id=\"tabProviders\"", index);
        // Three panels, one per tab; the inactive (tools, providers) ship hidden.
        Assert.Contains("id=\"panelGeneral\"", index);
        Assert.Contains("id=\"panelTools\"", index);
        Assert.Contains("id=\"panelProviders\"", index);
        // The default-tools container now lives in the Tools panel, not General: pin its position
        // between panelTools and panelProviders. Left in panelGeneral it would sort before panelTools.
        Assert.Contains("id=\"profileToolChecks\"", index);
        Assert.True(
            index.IndexOf("id=\"panelTools\"", StringComparison.Ordinal)
                < index.IndexOf("id=\"profileToolChecks\"", StringComparison.Ordinal),
            "profileToolChecks must appear after panelTools (i.e. inside the Tools panel).");
        Assert.True(
            index.IndexOf("id=\"profileToolChecks\"", StringComparison.Ordinal)
                < index.IndexOf("id=\"panelProviders\"", StringComparison.Ordinal),
            "profileToolChecks must appear before panelProviders.");
        Assert.Matches(@"role=""tabpanel""", index);
    }

    // The agent setup dialog is split into General + Tools + API key tabs (ADR 0021 tabs; the API-key
    // tab added later), sharing the Preferences tab pattern (source-text heuristic). This fact asserts
    // only the two original tabs; AgentEditor_HasApiKeyTab_WithAlwaysVisibleRunUrl covers the third.
    // Tab switching / keyboard nav is verified by driving DevHost, not here.
    [Fact]
    public async Task AgentDialog_HasGeneralAndToolsTabs()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"tabAgentGeneral\"", index);
        Assert.Contains("id=\"tabAgentTools\"", index);
        // Two panels, one per tab; the inactive (tools) ships hidden.
        Assert.Contains("id=\"panelAgentGeneral\"", index);
        Assert.Contains("id=\"panelAgentTools\"", index);
    }
}
