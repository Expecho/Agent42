using System.Text.Json;
using System.Text.Json.Nodes;
using Agent42.Agents;
using Agent42.Backend;
using ModelContextProtocol.Protocol;

namespace Agent42.Tests;

/// <summary>
/// MCP Apps capability and metadata (SEP-1865, ADR 0133): the extension is advertised only for a server with apps on,
/// by the one options helper BOTH connection factories call, and <c>_meta.ui</c> is parsed in both spellings.
/// </summary>
public sealed class McpAppsCapabilityTests
{
    // AC1/AC18: both factories take their client options from this helper (`McpElicitationClient.Options`), so one
    // assertion here covers the remote and the local-bridge path; the source guard below pins that neither calls it
    // without the server's flag.
    [Fact]
    public void Options_AdvertiseTheUiExtensionOnlyWhenAppsAreEnabled()
    {
        var off = McpElicitationClient.Options(new McpElicitationRouter());
        var on = McpElicitationClient.Options(new McpElicitationRouter(), appsEnabled: true);

        Assert.Null(off.Capabilities!.Extensions);
        var extension = Assert.IsType<JsonElement>(on.Capabilities!.Extensions![McpElicitationClient.AppsExtension]);
        Assert.Equal("text/html;profile=mcp-app", extension.GetProperty("mimeTypes")[0].GetString());
        // The elicitation declaration is untouched either way (ADR 0095).
        Assert.NotNull(on.Capabilities.Elicitation);
        Assert.NotNull(off.Capabilities.Elicitation);
    }

    // Measured in the Step 0 spike: the SDK's source-generated context must be able to write the capability, or the
    // very first request of the connection throws.
    [Fact]
    public void AdvertisedCapabilities_SerializeWithTheExtensionKey()
    {
        var options = McpElicitationClient.Options(new McpElicitationRouter(), appsEnabled: true);

        var json = JsonSerializer.Serialize(options.Capabilities, ModelContextProtocol.McpJsonUtilities.DefaultOptions);

        Assert.Contains("\"io.modelcontextprotocol/ui\"", json, StringComparison.Ordinal);
        Assert.Contains("text/html;profile=mcp-app", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BothFactories_PassTheServersFlagToTheOptions()
    {
        var root = RepoRoot();
        foreach (var file in new[] { "McpConnectionFactory.cs", "LocalBridgeConnectionFactory.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, "src", "Agent42.Backend", "Mcp", file));
            Assert.Contains("McpElicitationClient.Options(router, server.AppsEnabled)", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parse_ReadsTheNestedKey_AndDefaultsVisibility()
    {
        var meta = McpAppMeta.Parse(JsonNode.Parse("""{"ui":{"resourceUri":"ui://x/view.html"}}""") as JsonObject);

        Assert.Equal("ui://x/view.html", meta!.ResourceUri);
        Assert.Equal(["model", "app"], meta.Visibility);
        Assert.True(meta.VisibleToModel);
        Assert.True(meta.VisibleToApp);
    }

    [Fact]
    public void Parse_HonorsTheLegacyFlatKey()
    {
        var meta = McpAppMeta.Parse(JsonNode.Parse("""{"ui/resourceUri":"ui://x/old.html"}""") as JsonObject);

        Assert.Equal("ui://x/old.html", meta!.ResourceUri);
    }

    [Fact]
    public void Parse_AppOnlyVisibility_IsNotVisibleToTheModel()
    {
        var meta = McpAppMeta.Parse(JsonNode.Parse("""{"ui":{"visibility":["app"]}}""") as JsonObject);

        Assert.Null(meta!.ResourceUri);
        Assert.False(meta.VisibleToModel);
        Assert.True(meta.VisibleToApp);
    }

    // A tool with no `_meta`, or one that says nothing about ui, is unchanged — the common case must stay inert.
    [Fact]
    public void Parse_ReturnsNull_WhenTheToolDeclaresNothing()
    {
        Assert.Null(McpAppMeta.Parse(null));
        Assert.Null(McpAppMeta.Parse(JsonNode.Parse("""{"other":1}""") as JsonObject));
    }

    // Visibility is an allow list: a present, empty one hides the tool from both audiences.
    [Fact]
    public void Parse_EmptyVisibility_HidesFromBoth()
    {
        var meta = McpAppMeta.Parse(JsonNode.Parse("""{"ui":{"visibility":[]}}""") as JsonObject);

        Assert.False(meta!.VisibleToModel);
        Assert.False(meta.VisibleToApp);
    }

    [Fact]
    public void ToStored_DefaultsAppsOff()
    {
        var server = new StoredMcpServer("a", "n", "https://e", "None", true);

        Assert.False(server.AppsEnabled);
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
}
