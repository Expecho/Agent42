using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// MCP image results reach the model for the current turn only (the Chrome-extension ADR): the shaper in front of every
/// MCP tool takes images out of the result and records them in the run's <see cref="TurnContext"/>, and
/// <see cref="ToolImageChatClient"/> puts them back into the request. Nothing image-shaped is ever persisted.
/// </summary>
public sealed class McpToolImageTests
{
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x42, 0x42];

    private static TurnContext VisibleTurn()
    {
        var turn = TurnContext.ForChat(ephemeral: false, interactive: true, broker: null);
        turn.SetToolImagesVisible(true);
        return turn;
    }

    private static AIContent[] Screenshot(byte[]? bytes = null, string mediaType = "image/jpeg") =>
        [new TextContent("viewport 1280x720"), new DataContent(bytes ?? Jpeg, mediaType)];

    // AC 9d. The base64 never reaches the text that is persisted, and the bytes are kept for this run under the call id.
    [Fact]
    public void ImageBlock_IsExtracted_AndNeverReachesTheTextResult()
    {
        var turn = VisibleTurn();

        var text = Assert.IsType<string>(McpToolResultShaper.Shape(Screenshot(), turn, "call-1"));

        Assert.Contains("viewport 1280x720", text, StringComparison.Ordinal);
        Assert.Contains("[image 1: image/jpeg", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Jpeg), text, StringComparison.Ordinal);
        Assert.DoesNotContain("data:", text, StringComparison.Ordinal);
        var image = Assert.Single(turn.ToolImagesFor("call-1"));
        Assert.Equal(Jpeg, image.Data.ToArray());
    }

    [Theory]
    [InlineData("image/svg+xml", false)]
    [InlineData("image/png", false)]
    [InlineData("image/jpeg", true)]
    public void OversizeOrWrongTypeImage_IsDroppedWithANote(string mediaType, bool oversize)
    {
        var turn = VisibleTurn();
        var bytes = oversize ? [.. Jpeg, .. new byte[McpToolLimits.MaxImageBytes]] : Jpeg; // a JPEG is not a PNG

        var text = Assert.IsType<string>(McpToolResultShaper.Shape(Screenshot(bytes, mediaType), turn, "call-1"));

        Assert.Contains("[image 1 dropped:", text, StringComparison.Ordinal);
        Assert.False(turn.HasToolImages);
    }

    [Fact]
    public void NoTurnContext_DropsTheImage()
    {
        var text = Assert.IsType<string>(McpToolResultShaper.Shape(Screenshot(), TurnContext.Closed, "call-1"));

        Assert.Contains(McpToolResultShaper.ImageOmitted, text, StringComparison.Ordinal);
        Assert.False(TurnContext.Closed.HasToolImages);
    }

    // The Copilot path has no function-invoking client, so no call id: never keyed, never claimed as shown.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyCallId_IsNeverKeyed_AndSaysOmitted(string? callId)
    {
        var turn = VisibleTurn();

        var text = Assert.IsType<string>(McpToolResultShaper.Shape(Screenshot(), turn, callId));

        Assert.Contains(McpToolResultShaper.ImageOmitted, text, StringComparison.Ordinal);
        Assert.False(turn.HasToolImages);
    }

    // A copilot chat or a no-vision model: the grain sets visible=false, and the persisted placeholder says so.
    [Fact]
    public void ImagesNotVisible_PlaceholderSaysOmitted()
    {
        var turn = TurnContext.ForChat(ephemeral: false, interactive: true, broker: null);
        turn.SetToolImagesVisible(false);

        var text = Assert.IsType<string>(McpToolResultShaper.Shape(Screenshot(), turn, "call-1"));

        Assert.Contains(McpToolResultShaper.ImageOmitted, text, StringComparison.Ordinal);
        Assert.False(turn.HasToolImages);
    }

    // The pre-existing bug the spike found: a multi-part result's ToString() is its type name, so it was never cut.
    [Fact]
    public void MultiPartText_IsTruncatedLikeAnyOtherResult()
    {
        AIContent[] parts = [new TextContent(new string('a', 6000)), new TextContent(new string('b', 6000))];

        var text = Assert.IsType<string>(McpToolResultShaper.Shape(parts, TurnContext.Closed, "call-1"));

        Assert.Equal(McpToolLimits.MaxToolResultLength + McpToolLimits.TruncationMarker.Length, text.Length);
        Assert.EndsWith(McpToolLimits.TruncationMarker, text, StringComparison.Ordinal);
    }

    // A link block keeps its address, not the content type's name (ToString() of most AIContent is just that).
    [Fact]
    public void MultiPartLink_KeepsItsAddress()
    {
        AIContent[] parts = [new TextContent("see"), new UriContent(new Uri("https://example.com/report.pdf"), "application/pdf")];

        var text = Assert.IsType<string>(McpToolResultShaper.Shape(parts, TurnContext.Closed, "call-1"));

        Assert.Contains("[application/pdf: https://example.com/report.pdf]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Extensions.AI", text, StringComparison.Ordinal);
    }

    // A single text block keeps its ADR 0024 behaviour: the raw value when it fits.
    [Fact]
    public void SingleTextContent_PassesThroughUnchanged()
    {
        var raw = new TextContent("just text");

        Assert.Same(raw, McpToolResultShaper.Shape(raw, TurnContext.Closed, "call-1"));
    }

    [Fact]
    public void RecordedImages_AreCappedAtRecordTime()
    {
        var turn = VisibleTurn();
        for (var i = 1; i <= McpToolLimits.MaxImagesPerRun + 2; i++)
        {
            Assert.True(turn.RecordToolImage($"call-{i}", Jpeg, "image/jpeg"));
        }

        Assert.Empty(turn.ToolImagesFor("call-1"));
        Assert.Empty(turn.ToolImagesFor("call-2"));
        Assert.Single(turn.ToolImagesFor($"call-{McpToolLimits.MaxImagesPerRun + 2}"));
    }

    private static List<ChatMessage> Conversation(params string[] callIds) =>
    [
        new(ChatRole.User, "look at the page"),
        new(ChatRole.Assistant, [.. callIds.Select(id => (AIContent)new FunctionCallContent(id, "mcp_screenshot"))]),
        .. callIds.Select(id => new ChatMessage(ChatRole.Tool, [new FunctionResultContent(id, "[image 1: …]")])),
    ];

    // AC 9e. Option (b): one user message after the tool result, carrying the image.
    [Fact]
    public void ToolImageChatClient_InsertsTheImageAfterItsToolResult()
    {
        var turn = VisibleTurn();
        turn.RecordToolImage("call-1", Jpeg, "image/jpeg");

        var sent = ToolImageChatClient.WithToolImages(Conversation("call-1"), turn).ToList();

        Assert.Equal(4, sent.Count);
        Assert.Equal(ChatRole.User, sent[3].Role);
        Assert.Equal(ToolImageChatClient.Introduction, Assert.IsType<TextContent>(sent[3].Contents[0]).Text);
        var image = Assert.IsType<DataContent>(sent[3].Contents[1]);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(Jpeg, image.Data.ToArray());
    }

    // Never between two tool results: after the WHOLE group of a parallel call, once, with every image of the group.
    [Fact]
    public void ToolImageChatClient_InsertsAfterTheWholeParallelToolResultGroup()
    {
        var turn = VisibleTurn();
        turn.RecordToolImage("call-1", Jpeg, "image/jpeg");
        turn.RecordToolImage("call-2", Jpeg, "image/jpeg");
        var messages = Conversation("call-1", "call-2");
        messages.Add(new ChatMessage(ChatRole.Assistant, "seen both"));

        var sent = ToolImageChatClient.WithToolImages(messages, turn).ToList();

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Tool, ChatRole.User, ChatRole.Assistant], sent.Select(m => m.Role));
        Assert.Equal(2, sent[4].Contents.OfType<DataContent>().Count());
    }

    // A provider that reuses short call ids across turns: an earlier turn's result with this run's call id gets nothing.
    [Fact]
    public void ToolImageChatClient_IgnoresEarlierTurnsWithTheSameCallId()
    {
        var turn = VisibleTurn();
        turn.RecordToolImage("call_0", Jpeg, "image/jpeg");
        List<ChatMessage> messages = [.. Conversation("call_0"), new(ChatRole.Assistant, "done"), .. Conversation("call_0")];

        var sent = ToolImageChatClient.WithToolImages(messages, turn).ToList();

        Assert.Equal(messages.Count + 1, sent.Count);
        Assert.Equal(ChatRole.User, sent[^1].Role);
        Assert.Single(sent, m => m.Contents.OfType<DataContent>().Any());
    }

    // AC 9g. The handed messages are the grain's live session objects: an edit would persist base64 (ADR 0004).
    [Fact]
    public void ToolImageChatClient_NeverMutatesTheCallersMessages()
    {
        var turn = VisibleTurn();
        turn.RecordToolImage("call-1", Jpeg, "image/jpeg");
        var messages = Conversation("call-1");
        var before = messages.Select(m => (m, m.Contents.ToList())).ToList();

        var sent = ToolImageChatClient.WithToolImages(messages, turn).ToList();

        Assert.NotSame(messages, sent);
        Assert.Equal(3, messages.Count);
        foreach (var (message, contents) in before)
        {
            Assert.Equal(contents, message.Contents);
        }

        Assert.All(messages, m => Assert.DoesNotContain(m.Contents, c => c is DataContent));
    }

    [Fact]
    public void ToolImageChatClient_PassesThroughWhenThereIsNothingToShow()
    {
        var messages = Conversation("call-1");

        Assert.Same(messages, ToolImageChatClient.WithToolImages(messages, VisibleTurn()));
        Assert.Same(messages, ToolImageChatClient.WithToolImages(messages, TurnContext.Closed));
    }

    // Two runs can be in flight on one activation (rule 6): each sees only its own images.
    [Fact]
    public void TwoRunsOnOneActivation_DoNotSeeEachOthersImages()
    {
        var first = VisibleTurn();
        var second = VisibleTurn();
        first.RecordToolImage("call-1", Jpeg, "image/jpeg");

        var messages = Conversation("call-1");

        Assert.Same(messages, ToolImageChatClient.WithToolImages(messages, second));
        Assert.Empty(second.ToolImagesFor("call-1"));
    }

    // L1: OTel records message contents (EnableSensitiveData), so the images must be inserted BELOW it, innermost.
    [Fact]
    public void ToolImageChatClient_SitsBelowOpenTelemetry()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "Models", "ChatClientFactory.cs"));

        var otel = source.IndexOf(".UseOpenTelemetry(", StringComparison.Ordinal);
        var images = source.IndexOf(".Use(inner => new ToolImageChatClient(inner))", StringComparison.Ordinal);
        Assert.True(otel > 0 && images > otel, "ToolImageChatClient must be added after (inside) UseOpenTelemetry");
        Assert.DoesNotContain(".Use(", source[(images + 1)..source.IndexOf(".Build();", images, StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("new ToolImageChatClient(client)", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Agents", "Models", "IChatClientFactory.cs")), StringComparison.Ordinal);
    }

    // ADR 0104: the stable prefix (instructions, tools) is untouched; only the request's tail gains a message.
    [Fact]
    public void InstructionsPrefix_IsUnchangedByAnImage()
    {
        var turn = VisibleTurn();
        turn.RecordToolImage("call-1", Jpeg, "image/jpeg");
        var messages = Conversation("call-1");
        messages.Insert(0, new ChatMessage(ChatRole.System, "You are Agent 42."));

        var sent = ToolImageChatClient.WithToolImages(messages, turn).ToList();

        for (var i = 0; i < messages.Count; i++)
        {
            Assert.Same(messages[i], sent[i]);
        }
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

/// <summary>
/// The same path through the real grain and the test silo's single-client factory, which wraps the fake model in
/// <see cref="ToolImageChatClient"/> exactly as production does. The tool stands in for an MCP tool and runs the real
/// shaper, as <c>TruncatingMcpTool</c> does.
/// </summary>
public sealed class McpToolImageGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string ToolName = "mcp_screenshot";

    private void GiveScreenshotServer()
    {
        var tool = AIFunctionFactory.Create(
            () => McpToolResultShaper.Shape(
                new AIContent[] { new TextContent("viewport 1280x720"), new DataContent(McpToolImageTests.Jpeg, "image/jpeg") },
                TurnContext.CurrentOrClosed,
                FunctionInvokingChatClient.CurrentContext?.CallContent.CallId),
            ToolName,
            "takes a screenshot");
        foreach (var silo in Cluster.Silos)
        {
            var provider = Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<SingleUserMcpToolProvider>();
            provider.Servers = [new McpServerTools("browser", [tool], [ToolName], ServerName: "Browser")];
            provider.Version = Environment.TickCount;
        }
    }

    // Every silo's client: the conversation's grain may activate on either silo, so reading only the primary's counter
    // made "the image never reached the model" fail whenever placement chose the secondary.
    private IEnumerable<FakeChatClient> Fakes() =>
        Cluster.Silos.Select(s => Cluster.GetSiloServiceProvider(s.SiloAddress).GetRequiredService<FakeChatClient>());

    private void ResetFakes()
    {
        foreach (var fake in Fakes())
        {
            fake.Reset();
        }
    }

    private int MostImagePartsSeen() => Fakes().Max(f => f.MostImagePartsSeen);

    private async Task<string> SessionJsonOf(string grainKey)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", Cluster.GrainFactory.GetGrain<IAgentGrain>(grainKey).GetGrainId(), state);
        return state.State.SerializedSession ?? "";
    }

    private async Task ChatAsync(string grainKey, string model)
    {
        await foreach (var _ in Cluster.GrainFactory.GetGrain<IAgentGrain>(grainKey)
            .ChatAsync(FakeChatClient.CallToolPrompt(ToolName, new { }), model))
        {
        }
    }

    // AC 9g / Step 0(g4): the vision model received the image on the loop's next call; the session holds none of it.
    [Fact]
    public async Task StoredSession_HoldsNoImageBytes_AfterAScreenshotTurn()
    {
        GiveScreenshotServer();
        ResetFakes();
        const string key = "tool-image-user/conv";

        await ChatAsync(key, TestSiloConfigurator.DefaultModel);

        Assert.True(MostImagePartsSeen() >= 1, "the image never reached the model");
        var session = await SessionJsonOf(key);
        Assert.Contains("[image 1: image/jpeg", session, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(McpToolImageTests.Jpeg), session, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolImageChatClient.Introduction, session, StringComparison.Ordinal);
    }

    // AC 9f: a no-vision model is shown nothing, and the persisted placeholder says the image was omitted.
    [Fact]
    public async Task NoVisionModel_GetsNoImage_AndThePlaceholderSaysSo()
    {
        GiveScreenshotServer();
        ResetFakes();
        const string key = "tool-image-novision-user/conv";

        await ChatAsync(key, TestSiloConfigurator.NoVisionModel);

        Assert.Equal(0, MostImagePartsSeen());
        Assert.Contains(McpToolResultShaper.ImageOmitted, await SessionJsonOf(key), StringComparison.Ordinal);
    }
}
