using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// ADR 0121. The owner-only <c>/logs</c> console is a standalone page outside SHELL: a non-owner gets 404
    /// from the owner gate, and an owner's page must not enter the shared cache or fall back to the chat app.
    /// Source-text heuristic like the guards above: proves position, not logic.
    /// </summary>
    [Fact]
    public async Task ServiceWorker_BypassesTheLogsConsoleBeforeCaching()
    {
        var response = await _client.GetAsync("/sw.js");
        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStringAsync();

        var logsGuard = source.IndexOf("startsWith(\"/logs\")", StringComparison.Ordinal);
        Assert.True(logsGuard >= 0, "Service worker is missing the /logs bypass guard");

        var firstRespond = source.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(logsGuard < firstRespond, "The /logs bypass must precede any respondWith");
    }

    /// <summary>
    /// ADR 0097. The agent fleet page is standalone and outside SHELL: an offline <c>/fleet/</c> navigation must not
    /// fall through to the navigate branch, which would paint <c>/index.html</c> (the chat app) in its place, and
    /// its assets stay out of the shared cache. Source-text heuristic, like the guards above: proves position.
    /// </summary>
    [Fact]
    public async Task ServiceWorker_FleetNavigationNotServedFromShell()
    {
        var source = await _client.GetStringAsync("/sw.js");

        var fleetGuard = source.IndexOf("startsWith(\"/fleet\")", StringComparison.Ordinal);
        Assert.True(fleetGuard >= 0, "Service worker is missing the /fleet bypass guard");

        var firstRespond = source.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(fleetGuard < firstRespond, "The /fleet bypass must precede any respondWith");
        Assert.DoesNotContain("\"/fleet", source[..source.IndexOf("self.addEventListener", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// The public legal pages are standalone and outside SHELL, like <c>/fleet/</c>: an offline <c>/legal/</c>
    /// navigation must not paint <c>/index.html</c> in their place. Source-text heuristic: proves position.
    /// </summary>
    [Fact]
    public async Task ServiceWorker_LegalNavigationNotServedFromShell()
    {
        var source = await _client.GetStringAsync("/sw.js");

        var legalGuard = source.IndexOf("startsWith(\"/legal\")", StringComparison.Ordinal);
        Assert.True(legalGuard >= 0, "Service worker is missing the /legal bypass guard");

        var firstRespond = source.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(legalGuard < firstRespond, "The /legal bypass must precede any respondWith");
        Assert.DoesNotContain("\"/legal", source[..source.IndexOf("self.addEventListener", StringComparison.Ordinal)]);
    }

    [Theory]
    [InlineData("/legal/privacy.html", "Privacy policy")]
    [InlineData("/legal/terms.html", "Terms of service")]
    public async Task LegalPages_AreServed(string path, string heading)
    {
        var html = await _client.GetStringAsync(path);

        Assert.Contains($"<h1>{heading}</h1>", html);
    }

    /// <summary>
    /// ADR 0090. Easy Auth's <c>/.auth/*</c> endpoints are platform redirects, not shell. Without the bypass, an
    /// offline sign-out would fall through to the navigate branch and paint <c>/index.html</c>, so the app
    /// would look signed in. Source-text heuristic, like the guards above: proves position, not logic.
    /// </summary>
    [Fact]
    public async Task ServiceWorker_BypassesEasyAuthEndpointsBeforeCaching()
    {
        var response = await _client.GetAsync("/sw.js");
        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStringAsync();

        var authGuard = source.IndexOf("startsWith(\"/.auth\")", StringComparison.Ordinal);
        Assert.True(authGuard >= 0, "Service worker is missing the /.auth bypass guard");

        var firstRespond = source.IndexOf("event.respondWith(", StringComparison.Ordinal);
        Assert.True(authGuard < firstRespond, "The /.auth bypass must precede any respondWith");
    }

    /// <summary>
    /// ADR 0090. Sign out is how the second allowed account is reached once the login hint signs straight in.
    /// It must be Easy Auth's own logout, and it must land back on the app, which then redirects to Entra.
    /// </summary>
    [Fact]
    public async Task Sidebar_HasASignOutLinkToEasyAuthLogout()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains("id=\"signOutLink\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/.auth/logout?post_logout_redirect_uri=/\"", html, StringComparison.Ordinal);
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
    public async Task IndexHtml_LinksManifest_AndAppRegistersServiceWorker()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Contains("rel=\"manifest\"", index);
        Assert.Contains("theme-color", index);

        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("serviceWorker.register", app);
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
        Assert.Contains("agent42-shell-v90", sw);
    }

    // Palette axis (ADR 0129): source-text proof that the picker, the engine, the head scripts and the offline
    // cache all shipped. Runtime behaviour is walked by hand in the DevHost (no JS runner by design).
    [Fact]
    public async Task Palette_IsWiredThroughPickerEngineHeadScriptsAndShell()
    {
        var index = await _client.GetStringAsync("/index.html");
        Assert.Contains("id=\"profilePalette\"", index);
        Assert.Contains("/shared/themes.css", index);
        Assert.Contains("dataset.palette", index); // no-FOUC head script
        Assert.Contains("agent42.palette", index);
        Assert.True(index.IndexOf("/shared/themes.css", StringComparison.Ordinal) < index.IndexOf("dataset.palette", StringComparison.Ordinal),
            "the head script must come after the themes.css link, so --bg is resolved when it reads it");

        var theme = await _client.GetStringAsync("/js/theme.js");
        Assert.Contains("export function applyPalette", theme);
        Assert.Contains("agent42.palette", theme);

        var profile = await _client.GetStringAsync("/js/profile.js");
        Assert.Contains("palette: profilePalette.value", profile);
        Assert.Contains("applyPalette(", profile);

        var sw = await _client.GetStringAsync("/sw.js");
        var shell = Regex.Match(sw, @"const SHELL = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("\"/shared/themes.css\"", shell);

        foreach (var page in new[] { "/fleet/index.html", "/logs/index.html" })
        {
            var html = await _client.GetStringAsync(page);
            Assert.Contains("/shared/themes.css", html);
            Assert.Contains("agent42.palette", html);
        }
    }

    // The public recipient pages have no profile: they stay amber + OS mode and never read the user's cache.
    // They still load themes.css (viewer.css no longer carries amber), and the amber light fallback in themes.css
    // keeps them readable when only data-theme is set.
    [Theory]
    [InlineData("/shared/index.html")]
    [InlineData("/shared/fleet.html")]
    [InlineData("/legal/privacy.html")]
    [InlineData("/legal/terms.html")]
    public async Task PublicPages_DoNotReadTheUsersPalette(string page)
    {
        var html = await _client.GetStringAsync(page);
        Assert.DoesNotContain("agent42.palette", html);
        Assert.DoesNotContain("dataset.palette", html);
    }

    [Theory]
    [InlineData("/shared/index.html")]
    [InlineData("/shared/fleet.html")]
    public async Task SharedPages_LoadThemesCss(string page)
        => Assert.Contains("href=\"themes.css\"", await _client.GetStringAsync(page));

    // Rich rendering (ADR 0083): SHELL holds only the eager renderer + highlight.js. The lazy libs stay out —
    // SHELL is one all-or-nothing addAll, and a 5 MB mermaid there slows install and adds a failure point.
    // They are still cached at runtime by stale-while-revalidate on first use.
    [Fact]
    public async Task ServiceWorkerShell_OmitsLazyLibs()
    {
        var sw = await _client.GetStringAsync("/sw.js");
        var match = Regex.Match(sw, @"const SHELL = \[(.*?)\];",
            RegexOptions.Singleline);
        Assert.True(match.Success, "sw.js no longer declares a SHELL array");
        // The entries only — the array's comments name the lazy libs on purpose.
        var entries = Regex.Matches(match.Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();

        Assert.Contains("/shared/lib/richtext.js", entries);
        Assert.Contains("/shared/lib/highlight/highlight.min.js", entries);
        // The artifact pane script is eager (index.html loads it as a classic script), so it IS in SHELL: a missing
        // entry would leave an offline app with no cards rather than with a lazy load that retries.
        Assert.Contains("/shared/lib/artifactpane.js", entries);
        Assert.DoesNotContain(entries, e => e.Contains("mermaid", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Contains("katex", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Contains("/chart/", StringComparison.Ordinal));
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

    // ADR 0144: a fleet push carries a url (/fleet/?task={id}). The worker keeps it in the notification's data, accepts it
    // only when same-origin, and on a click navigates or opens a window there INSTEAD of posting open-conversation.
    [Fact]
    public async Task Sw_NotificationClick_OpensUrl()
    {
        var sw = await _client.GetStringAsync("/sw.js");

        Assert.Contains("url: data.url || \"\"", sw);
        Assert.Contains("parsed.origin === self.location.origin", sw);
        var click = sw.IndexOf("addEventListener(\"notificationclick\"", StringComparison.Ordinal);
        var urlBranch = sw.IndexOf("if (url) {", click, StringComparison.Ordinal);
        var openConversation = sw.IndexOf("open-conversation", click, StringComparison.Ordinal);
        Assert.True(urlBranch > click && urlBranch < openConversation, "The url branch must come before the conversation message");
        Assert.Contains("client.navigate(url)", sw);
        // A chat window is never navigated away: only a window already on /fleet/ is reused.
        Assert.Contains("new URL(c.url).pathname.startsWith(\"/fleet/\")", sw);
        Assert.Contains("openWindow(url)", sw);
    }

}
