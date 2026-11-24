using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary><c>generate_image</c> for a fleet task (ADR 0144): capability-gated, writes into the workspace, never returns bytes, capped.</summary>
public sealed class FleetImageToolsTests : IDisposable
{
    private static readonly ScriptedFleetInteraction Interaction = new();

    private readonly FleetWorkspace _workspace = new(
        Guid.NewGuid().ToString("N"), root: Path.Combine(Path.GetTempPath(), "agent42-image-" + Guid.NewGuid().ToString("N")));

    // "img" is an image model with the canned 1x1 PNG generator; anything else is a chat model.
    private static readonly IChatClientFactory Models = new SingleChatClientFactory(
        new FakeChatClient(),
        capabilities: id => id == "img" ? new ModelCapabilities(false, false, SupportsImageGeneration: true) : ModelCapabilities.Full,
        imageGenerator: new InMemoryImageGenerator());

    public void Dispose() => _workspace.Dispose();

    private Task<FleetToolSet> BuildAsync(IReadOnlyList<string> imageModels, IChatClientFactory? models = null)
        => new FleetToolCatalog(null!, NullLogger<FleetToolCatalog>.Instance).BuildAsync(
            new FleetToolContext("alice", "t1", "", [], Interaction) { Workspace = _workspace, Models = models ?? Models, ImageModels = imageModels },
            default);

    private static async Task<string> GenerateAsync(FleetToolSet set, string prompt = "a red fox", string? model = null)
        => (await set.Tools.Single(t => t.Name == FleetImageTools.Name).InvokeAsync(new() { ["prompt"] = prompt, ["model"] = model }))!.ToString()!;

    [Fact]
    public async Task Image_NoImageModel_Absent()
    {
        var set = await BuildAsync([]);

        Assert.DoesNotContain(set.Tools, t => t.Name == FleetImageTools.Name);
    }

    /// <summary>Gated by capability, not by a selection: an empty override still has it.</summary>
    [Fact]
    public async Task Image_WritesWorkspaceFile()
    {
        var set = await BuildAsync(["img"]);

        var result = await GenerateAsync(set);

        var path = Assert.Single(_workspace.ListFiles());
        Assert.StartsWith("images/", path);
        Assert.EndsWith(".png", path);
        Assert.Contains(path, result);
        Assert.True(result.Length < 200, "The result names the file, it never carries the bytes");
        Assert.DoesNotContain("iVBOR", result);
    }

    [Fact]
    public async Task Image_Cap()
    {
        var set = await BuildAsync(["img"]);

        for (var i = 0; i < FleetImageTools.MaxImagesPerTask; i++)
        {
            await GenerateAsync(set);
        }

        var refused = await GenerateAsync(set);

        Assert.Contains("the limit", refused);
        Assert.Equal(FleetImageTools.MaxImagesPerTask, _workspace.ListFiles().Count);
    }

    [Fact]
    public async Task Image_UnknownModel_Refused()
    {
        var set = await BuildAsync(["img"]);

        var refused = await GenerateAsync(set, model: "chat");

        Assert.Contains("is not one of the image models", refused);
        Assert.Empty(_workspace.ListFiles());
    }
}
