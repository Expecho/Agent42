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
    private readonly HttpClient _client;

    public PwaAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

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
        var app = await _client.GetStringAsync("/app.js");
        Assert.Contains("chunkCount > 0", app);
        Assert.Contains("attach-chip--warn", app);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".attach-chip--warn", styles);
    }

    [Fact]
    public async Task Frontend_GuardsUploadResultAgainstMidUploadConversationSwitch()
    {
        // Mode B: an upload is keyed to the conversation it started in, but its result lands
        // whenever the await resolves — a mid-upload switch would inject the chip into the wrong
        // conversation. handleFiles must pin uploadId at entry and bail when activeId moves on.
        // Source-text heuristic (no JS test runner by design) — proves the guard exists.
        var app = await _client.GetStringAsync("/app.js");
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
        var app = await _client.GetStringAsync("/app.js");
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

        var app = await _client.GetStringAsync("/app.js");
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
        // and sets data-theme before app.js). Both must exist or the picker/anti-flash break.
        Assert.Contains("id=\"profileTheme\"", index);
        Assert.Contains("agent42.theme", index);
        Assert.Contains("data-theme", index);

        var app = await _client.GetStringAsync("/app.js");
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
        // Bumped whenever the cached shell (index/styles/app) changes, so returning users don't get
        // stale assets — sw.js serves the shell cache-first with background revalidate.
        var sw = await _client.GetStringAsync("/sw.js");
        Assert.Contains("agent42-shell-v7", sw);
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
        // AC5 — structural/weak check (Critic): handleFiles pervades app.js, so this proves the share
        // hand-off strings exist AND that applyShare routes the shared files through handleFiles
        // (position-bounded, mutation-observable). The real proof is the manual DevHost drive (AC6).
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("has(\"share\")", app);   // the /?share=1 load-time hand-off detection
        Assert.Contains("readSharePayload", app); // reads the IndexedDB stash the SW wrote

        // handleFiles must be called INSIDE applyShare, not merely present somewhere in the file.
        var applyShare = app.IndexOf("function applyShare", StringComparison.Ordinal);
        Assert.True(applyShare >= 0, "app.js is missing the applyShare share handler");
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

    // The Preferences dialog is split into General + Model providers tabs (source-text heuristic).
    // Tab switching / keyboard nav is verified by driving the DevHost UI, not asserted here.
    [Fact]
    public async Task Preferences_HasGeneralAndProvidersTabs()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("role=\"tablist\"", index);
        Assert.Contains("id=\"tabGeneral\"", index);
        Assert.Contains("id=\"tabProviders\"", index);
        // Two panels, one per tab; the inactive (providers) ships hidden.
        Assert.Contains("id=\"panelGeneral\"", index);
        Assert.Contains("id=\"panelProviders\"", index);
        Assert.Matches(@"role=""tabpanel""", index);
    }

    // The agent setup dialog is split into General + Tools tabs, sharing the Preferences tab pattern
    // (source-text heuristic). Tab switching / keyboard nav is verified by driving DevHost, not here.
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
