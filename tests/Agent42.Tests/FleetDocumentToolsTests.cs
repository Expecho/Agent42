using System.Text;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The fleet's document tools (ADR 0101): <c>search_documents</c> and <c>read_document</c> reach one workspace's
/// documents only, are capped, and turn every failure into a refusal string instead of throwing into the run.
/// </summary>
public sealed class FleetDocumentToolsTests
{
    private const string Alice = "alice";

    private readonly InMemoryDocumentIndex _index = new();

    private static IUserChatClientFactoryProvider Provider(IEmbeddingGenerator<string, Embedding<float>>? embeddings)
        => new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(NoModelChatClient.Instance, embeddingGenerator: embeddings));

    private async Task IngestAsync(string ownerKey, string fileName, string text)
        => await new DocumentIngestionService(new DocumentTextExtractor(), new DocumentChunker(), Provider(new DeterministicEmbeddingGenerator()), _index)
            .IngestAsync(ownerKey, fileName, DocumentMediaTypes.Markdown, Encoding.UTF8.GetBytes(text));

    private FleetDocumentTools Tools(IEmbeddingGenerator<string, Embedding<float>>? embeddings, params string[] documents)
        => new(Provider(embeddings), _index, Alice, "w1", documents, NullLogger.Instance);

    private static async Task<string> InvokeAsync(FleetDocumentTools tools, string name, AIFunctionArguments arguments)
        => (await tools.Tools.Single(t => t.Name == name).InvokeAsync(arguments))!.ToString()!;

    [Fact]
    public async Task Search_ScopedToWorkspace_Capped()
    {
        await IngestAsync(FleetTaskService.DocumentOwnerKey(Alice, "w1"), "guide.md", "Magrathea builds luxury planets for the very rich.");
        await IngestAsync(FleetTaskService.DocumentOwnerKey(Alice, "w2"), "other.md", "Magrathea is in another workspace entirely.");
        await IngestAsync($"{Alice}/conversation-1", "chat.md", "Magrathea in a chat conversation.");
        var tools = Tools(new DeterministicEmbeddingGenerator(), "guide.md");

        var result = await InvokeAsync(tools, FleetDocumentTools.SearchName, new() { ["query"] = "Magrathea planets" });

        Assert.Contains("[guide.md, chunk 0]", result);
        Assert.Contains("luxury planets", result);
        Assert.DoesNotContain("other.md", result);
        Assert.DoesNotContain("chat.md", result);
        Assert.True(result.Length <= FleetDocumentTools.MaxSearchTextLength);
    }

    [Fact]
    public async Task Search_ProviderFailure_Refuses()
    {
        await IngestAsync(FleetTaskService.DocumentOwnerKey(Alice, "w1"), "guide.md", "Magrathea builds luxury planets.");

        var failing = await InvokeAsync(Tools(new ThrowingEmbeddings(), "guide.md"), FleetDocumentTools.SearchName, new() { ["query"] = "planets" });
        var noModel = await InvokeAsync(Tools(null, "guide.md"), FleetDocumentTools.SearchName, new() { ["query"] = "planets" });

        Assert.StartsWith("The document search failed: provider down", failing);
        Assert.Contains("no embedding model", noModel);
    }

    [Fact]
    public async Task Read_InOrder_RefusesUnknown()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i}: " + string.Concat(Enumerable.Repeat("Arthur Dent walks. ", 20))));
        await IngestAsync(FleetTaskService.DocumentOwnerKey(Alice, "w1"), "guide.md", text);
        await IngestAsync(FleetTaskService.DocumentOwnerKey(Alice, "w1"), "hidden.md", "Not in this task's Documents.");
        var tools = Tools(new DeterministicEmbeddingGenerator(), "guide.md");

        var first = await InvokeAsync(tools, FleetDocumentTools.ReadName, new() { ["fileName"] = "guide.md", ["part"] = 1 });
        var unknown = await InvokeAsync(tools, FleetDocumentTools.ReadName, new() { ["fileName"] = "hidden.md" });

        Assert.StartsWith(ReadDocumentLimits.PartHeaderPrefix + "guide.md' — part 1 of ", first);
        Assert.Contains("Paragraph 1:", first);
        Assert.EndsWith("Call read_document with part 2 to continue.", first);
        Assert.DoesNotContain("UNTRUSTED", first);
        Assert.Equal("There is no document named 'hidden.md' in this task. Use one of these names exactly: 'guide.md'.", unknown);

        var parts = int.Parse(first.Split(" part 1 of ")[1].Split('\n')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var last = await InvokeAsync(tools, FleetDocumentTools.ReadName, new() { ["fileName"] = "guide.md", ["part"] = parts });
        Assert.Contains("Paragraph 40:", last);
        Assert.EndsWith("This is the last part.", last);
    }

    private sealed class ThrowingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("provider down");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
