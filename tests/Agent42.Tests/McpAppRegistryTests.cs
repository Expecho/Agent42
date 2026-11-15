using System.Text.Json;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The registry's rules (ADR 0133): what a view may read and call, over a stub handle source. Every refusal here is a
/// defence against a third-party view, so each has its own test.
/// </summary>
public sealed class McpAppRegistryTests
{
    private const string Html = "text/html;profile=mcp-app";

    private static (McpAppRegistry Registry, StubClient Client, StubSource Source) Build(
        string mime = Html, string html = "<html></html>", bool available = true)
    {
        var client = new StubClient { Resource = new McpAppResource(mime, html, """{"csp":{}}""") };
        var source = new StubSource(available ? new McpAppHandle(
            client,
            new HashSet<string> { "show", "refresh", "model_only", "plain" },
            new Dictionary<string, McpAppToolMeta>
            {
                ["show"] = new("ui://a/v", ["model", "app"]),
                ["refresh"] = new(null, ["app"]),
                ["model_only"] = new(null, ["model"]),
            }) : null);
        return (new McpAppRegistry(source, NullLogger<McpAppRegistry>.Instance), client, source);
    }

    // AC3: an app-visible tool is callable; a model-only tool is refused; a tool with no `_meta` follows the spec
    // default (visible to both); an unknown name is a bad request, not a pass-through.
    [Fact]
    public async Task CallTool_RespectsVisibility()
    {
        var (registry, client, _) = Build();

        Assert.True((await registry.CallToolAsync("u", "s", "refresh", null, default)).Ok);
        Assert.True((await registry.CallToolAsync("u", "s", "plain", null, default)).Ok);
        Assert.Equal(McpAppError.Forbidden, (await registry.CallToolAsync("u", "s", "model_only", null, default)).Error);
        Assert.Equal(McpAppError.BadRequest, (await registry.CallToolAsync("u", "s", "nope", null, default)).Error);
        Assert.Equal(["refresh", "plain"], client.Calls);
    }

    // AC3: the handle is resolved for the server named in the request only, so a view cannot reach another server.
    [Fact]
    public async Task CallTool_ResolvesOnlyTheRequestedServer()
    {
        var (registry, _, source) = Build();

        await registry.CallToolAsync("user-1", "server-9", "refresh", null, default);

        Assert.Equal([("user-1", "server-9")], source.Requests);
    }

    // AC3/AC19: a disabled, apps-off or unreachable server has no handle, and the registry fails closed.
    [Fact]
    public async Task UnavailableServer_FailsClosed()
    {
        var (registry, client, _) = Build(available: false);

        Assert.Equal(McpAppError.Unavailable, (await registry.CallToolAsync("u", "s", "refresh", null, default)).Error);
        Assert.Equal(McpAppError.Unavailable, (await registry.ReadUiResourceAsync("u", "s", "ui://a/v", default)).Error);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task CallTool_RefusesOversizedArguments_AndResults()
    {
        var (registry, client, _) = Build();
        using var big = JsonDocument.Parse("\"" + new string('a', McpAppLimits.MaxCallArgumentBytes) + "\"");

        Assert.Equal(McpAppError.TooLarge, (await registry.CallToolAsync("u", "s", "refresh", big.RootElement, default)).Error);
        Assert.Empty(client.Calls);

        client.Result = JsonDocument.Parse("\"" + new string('b', McpAppLimits.MaxCallResultBytes) + "\"").RootElement;
        Assert.Equal(McpAppError.TooLarge, (await registry.CallToolAsync("u", "s", "refresh", null, default)).Error);
    }

    [Fact]
    public async Task CallTool_ReportsAServerFailure_WithoutLeakingIt()
    {
        var (registry, client, _) = Build();
        client.Throw = new InvalidOperationException("secret detail from the server");

        Assert.Equal(McpAppError.Failed, (await registry.CallToolAsync("u", "s", "refresh", null, default)).Error);
    }

    // AC4: the view must be a ui:// resource, with the MCP App MIME type, within the size cap.
    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("resource://a/b")]
    [InlineData("")]
    public async Task ReadUiResource_RefusesANonUiScheme(string uri)
    {
        var (registry, client, _) = Build();

        Assert.Equal(McpAppError.BadRequest, (await registry.ReadUiResourceAsync("u", "s", uri, default)).Error);
        Assert.Empty(client.Reads);
    }

    [Fact]
    public async Task ReadUiResource_RefusesTheWrongMime()
    {
        var (registry, _, _) = Build(mime: "text/html");

        Assert.Equal(McpAppError.BadRequest, (await registry.ReadUiResourceAsync("u", "s", "ui://a/v", default)).Error);
    }

    [Fact]
    public async Task ReadUiResource_RefusesAnOversizedView()
    {
        var (registry, _, _) = Build(html: new string('x', McpAppLimits.MaxViewBytes + 1));

        Assert.Equal(McpAppError.TooLarge, (await registry.ReadUiResourceAsync("u", "s", "ui://a/v", default)).Error);
    }

    [Fact]
    public async Task ReadUiResource_ReturnsTheViewAndItsDeclaredMeta()
    {
        var (registry, _, _) = Build();

        var outcome = await registry.ReadUiResourceAsync("u", "s", "ui://a/v", default);

        Assert.True(outcome.Ok);
        Assert.Equal("<html></html>", outcome.Value!.Html);
        Assert.Equal("""{"csp":{}}""", outcome.Value.UiMetaJson);
    }

    // A view is read once per connection: the second render is served from the cache, and a new connection (a new
    // handle, as after a config save) reads again so a changed server is never shown through a stale entry.
    [Fact]
    public async Task ReadUiResource_IsCachedPerConnection()
    {
        var (registry, client, source) = Build();

        await registry.ReadUiResourceAsync("u", "s", "ui://a/v", default);
        await registry.ReadUiResourceAsync("u", "s", "ui://a/v", default);
        Assert.Single(client.Reads);

        source.Handle = source.Handle! with { };
        await registry.ReadUiResourceAsync("u", "s", "ui://a/v", default);
        Assert.Equal(2, client.Reads.Count);
    }

    private sealed class StubSource(McpAppHandle? handle) : IMcpAppHandleSource
    {
        public McpAppHandle? Handle { get; set; } = handle;

        public List<(string User, string Server)> Requests { get; } = [];

        public Task<McpAppHandle?> GetAppHandleAsync(string userId, string serverId, CancellationToken cancellationToken)
        {
            Requests.Add((userId, serverId));
            return Task.FromResult(Handle);
        }
    }

    private sealed class StubClient : IMcpAppClient
    {
        public McpAppResource Resource { get; set; } = new(null, null, null);

        public JsonElement Result { get; set; } = JsonDocument.Parse("{}").RootElement;

        public Exception? Throw { get; set; }

        public List<string> Calls { get; } = [];

        public List<string> Reads { get; } = [];

        public Task<McpAppResource> ReadResourceAsync(string uri, CancellationToken cancellationToken)
        {
            Reads.Add(uri);
            return Task.FromResult(Resource);
        }

        public Task<JsonElement> CallToolAsync(string toolName, JsonElement? arguments, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add(toolName);
            return Task.FromResult(Result);
        }
    }
}
