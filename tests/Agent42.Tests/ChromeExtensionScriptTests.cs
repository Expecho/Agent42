using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The Agent42 Chrome extension (<c>tools/Agent42.ChromeExtension</c>) — a second local-bridge client (ADR 0076
/// and the Chrome-extension ADR). Its logic lives in chrome-free ES modules under <c>lib/</c> with a
/// <c>node --test</c> suite, which this class runs so <c>dotnet test</c> (and CI) cover it. The rest pins what
/// the extension and the backend must agree on, in source text, so a change on one side fails here rather than
/// on the wire. The coverage gate measures C# only: none of the JS is counted, and none is claimed.
/// </summary>
public sealed partial class ChromeExtensionScriptTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Extension(params string[] parts) => Read(["tools", "Agent42.ChromeExtension", .. parts]);

    private static JsonElement Manifest() => JsonDocument.Parse(Extension("manifest.json")).RootElement;

    // The whole node suite: the gate, the bridge loop, the MCP server, the tools, the refusals and the options parsing.
    [Fact]
    public void ExtensionNodeTests_Pass()
    {
        var (versionExit, version) = Run("node", "--version");
        Assert.True(versionExit == 0, "node is required on PATH to run the extension's tests");
        var major = int.Parse(NodeMajor().Match(version).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(major >= 22, $"the extension's tests need Node 22 or later (global WebSocket, node --test globs); found {version.Trim()}");

        // Since Node 21 the arguments are globs, so a bare directory is not expanded: name the files.
        var (exit, output) = Run("node", "--test", "tools/Agent42.ChromeExtension/test/**/*.test.mjs");

        Assert.True(exit == 0, output);
        Assert.Matches(@"# fail 0", output);
    }

    // The extension id is derived from the public key in the manifest, and the backend pins that id as the one
    // Origin allowed to present a bridge key in the subprotocol. Regenerating the key without updating
    // LocalBridgeLimits would lock the extension out.
    [Fact]
    public void ExtensionId_MatchesManifestKey()
    {
        var key = Convert.FromBase64String(Manifest().GetProperty("key").GetString()!);
        var hex = Convert.ToHexStringLower(SHA256.HashData(key))[..32];
        var id = new string([.. hex.Select(h => (char)('a' + Convert.ToInt32(h.ToString(), 16)))]);

        Assert.Equal($"chrome-extension://{id}", LocalBridgeLimits.ExtensionOrigin);
    }

    // The cross-language constants. A mismatch in any of these fails silently on the wire.
    [Fact]
    public void Limits_MatchTheBackend()
    {
        var limits = Extension("lib", "limits.js");

        Assert.Equal(LocalBridgeLimits.ExtensionSubprotocol, JsString(limits, "SUBPROTOCOL"));
        Assert.Equal(LocalBridgeLimits.ReplacedCloseReason, JsString(limits, "REPLACED_CLOSE_REASON"));
        Assert.Equal(LocalBridgeEndpoint.Route, JsString(limits, "ROUTE"));
        Assert.Equal(LocalBridgeEnvelope.ControlChannel, JsNumber(limits, "CONTROL_CHANNEL"));
        Assert.Equal(LocalBridgeLimits.MaxChannels, JsNumber(limits, "MAX_CHANNELS"));
        Assert.Equal(LocalBridgeLimits.MaxMessageBytes, JsNumber(limits, "MAX_FRAME_BYTES"));
    }

    // Results are cut by the extension, with a paging hint, before the backend's truncation could cut them silently.
    [Fact]
    public void ResultCap_IsBelowTheBackendTruncation()
    {
        var cap = JsNumber(Extension("lib", "limits.js"), "MAX_RESULT_CHARS");

        Assert.True(cap + 512 < McpToolLimits.MaxToolResultLength, $"MAX_RESULT_CHARS {cap} leaves too little room under {McpToolLimits.MaxToolResultLength}");
    }

    // A screenshot frame must fit the bridge's frame cap with room for the envelope and the text block.
    [Fact]
    public void ScreenshotCap_FitsAFrame()
        => Assert.True(JsNumber(Extension("lib", "limits.js"), "MAX_SCREENSHOT_BASE64") + 64 * 1024 < LocalBridgeLimits.MaxMessageBytes);

    // What a web page could use to reach the extension: content scripts, external messaging, and resources it
    // could frame (the approval page in an iframe would be a clickjacking target). None may exist. And no
    // declarativeNetRequest: measured, it never touches the extension's own socket, and a broad rule put the key
    // on WEB PAGES' sockets instead.
    [Fact]
    public void Manifest_GivesWebPagesNoWayIn()
    {
        var manifest = Manifest();

        Assert.Equal(3, manifest.GetProperty("manifest_version").GetInt32());
        foreach (var forbidden in new[] { "content_scripts", "externally_connectable", "web_accessible_resources" })
        {
            Assert.False(manifest.TryGetProperty(forbidden, out _), $"manifest.json must not declare {forbidden}");
        }

        var permissions = manifest.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.DoesNotContain(permissions, p => p!.StartsWith("declarativeNetRequest", StringComparison.Ordinal));
    }

    // Injected functions run in the page's isolated world, where chrome.storage.local (the bridge key) is readable.
    // They must take only their arguments.
    [Fact]
    public void InjectedFunctions_NeverTouchExtensionApis()
    {
        var browser = Extension("browser.js");
        var start = browser.IndexOf("// ---- injected", StringComparison.Ordinal);
        var end = browser.IndexOf("// ---- keys", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "browser.js lost its injected-functions section markers");

        Assert.DoesNotContain("chrome.", browser[start..end], StringComparison.Ordinal);
    }

    // The approval request is attacker-influenced text. It is only ever written as text, never parsed as markup.
    [Fact]
    public void ApprovalPage_WritesTextOnly()
    {
        var approve = Extension("approve.js");

        Assert.DoesNotContain("innerHTML", approve, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", approve, StringComparison.Ordinal);
        Assert.Contains(".textContent = request.tool", approve, StringComparison.Ordinal);
        Assert.Contains(".textContent = request.args", approve, StringComparison.Ordinal);
    }

    // Only the extension's own approval page may answer a prompt.
    [Fact]
    public void ApprovalAnswers_AreAcceptedOnlyFromTheApprovalPage()
    {
        var background = Extension("background.js");

        Assert.Contains("sender.id === chrome.runtime.id", background, StringComparison.Ordinal);
        Assert.Contains("sender.url.startsWith(`${APPROVE_PAGE}?`)", background, StringComparison.Ordinal);
        Assert.Matches(@"message\?\.type === ""approval-answer"" && fromApprovePage", background);
    }

    private static string JsString(string source, string name)
    {
        var match = Regex.Match(source, $@"export const {name} = ""([^""]*)"";");
        Assert.True(match.Success, $"{name} not found as a string constant in limits.js");
        return match.Groups[1].Value;
    }

    private static int JsNumber(string source, string name)
    {
        var match = Regex.Match(source, $@"export const {name} = ([0-9_ *]+);");
        Assert.True(match.Success, $"{name} not found as a numeric constant in limits.js");
        return match.Groups[1].Value.Replace("_", "", StringComparison.Ordinal).Split('*')
            .Select(p => int.Parse(p.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .Aggregate(1, (a, b) => a * b);
    }

    private static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(TimeSpan.FromMinutes(2)), "the node test run did not finish in 2 minutes");
        return (proc.ExitCode, stdout.Result + stderr.Result);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    [GeneratedRegex(@"v(\d+)\.")]
    private static partial Regex NodeMajor();
}
