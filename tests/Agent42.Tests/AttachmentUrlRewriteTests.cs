using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// AC8 (rewrite half): the persisted session keeps the tiny reference URL, and the request that reaches the model carries
/// the bytes. Covers every provider kind the factory builds, and the ceiling behavior (placeholder, oldest first, never a
/// failed turn).
/// </summary>
public sealed class AttachmentUrlRewriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent42-rewrite-" + Guid.NewGuid().ToString("N"));
    private readonly FileAttachmentStore _store;

    public AttachmentUrlRewriteTests() => _store = new FileAttachmentStore(new LocalFileStore(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AttachmentInliner Inliner(long perImage = 1024, long total = 4096)
        => new(_store, new AttachmentInlineOptions(perImage, total));

    private async Task<(AttachmentRef Ref, UriContent Content)> SaveImageAsync(int size, string owner = "u/c")
    {
        var saved = await _store.SaveAsync(owner, "i.png", "image/png", Enumerable.Repeat((byte)7, size).ToArray());
        return (saved, new UriContent(await _store.GetReadUrlAsync(saved), saved.MediaType));
    }

    private static ChatMessage User(string text, params AIContent[] images)
        => new(ChatRole.User, [new TextContent(text), .. images]);

    private sealed class RecordingClient : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task TheRequestHoldsBytes_AndThePersistedMessagesStayUntouched()
    {
        var (_, image) = await SaveImageAsync(10);
        var persisted = User("look", image);
        var recorder = new RecordingClient();

        await new AttachmentInlineChatClient(recorder, Inliner()).GetResponseAsync([persisted]);

        var sent = Assert.Single(recorder.Seen);
        var data = Assert.IsType<DataContent>(sent.Contents[1]);
        Assert.Equal(10, data.Data.Length);
        Assert.Equal("image/png", data.MediaType);
        Assert.IsType<TextContent>(sent.Contents[0]);

        // The session still holds the reference: same instance, same URL, no bytes.
        Assert.Same(image, persisted.Contents[1]);
        Assert.Equal(FileAttachmentStore.ReferenceScheme, image.Uri!.Scheme);
    }

    [Fact]
    public async Task TheStreamingPath_IsRewrittenToo()
    {
        var (_, image) = await SaveImageAsync(5);
        var recorder = new RecordingClient();

        await foreach (var _ in new AttachmentInlineChatClient(recorder, Inliner()).GetStreamingResponseAsync([User("x", image)]))
        {
        }

        Assert.IsType<DataContent>(Assert.Single(recorder.Seen).Contents[1]);
    }

    [Fact]
    public async Task AnImageOverThePerImageCeiling_BecomesAPlaceholder_AndTheTurnProceeds()
    {
        var (_, big) = await SaveImageAsync(2000);
        var recorder = new RecordingClient();

        var response = await new AttachmentInlineChatClient(recorder, Inliner(perImage: 1024)).GetResponseAsync([User("x", big)]);

        Assert.Equal("ok", response.Text);
        var text = Assert.IsType<TextContent>(Assert.Single(recorder.Seen).Contents[1]);
        Assert.Equal(AttachmentInliner.Placeholder, text.Text);
    }

    [Fact]
    public async Task PastTheCumulativeBudget_TheOldestImagesGiveWay_NewestKept()
    {
        var (_, oldest) = await SaveImageAsync(1000);
        var (_, middle) = await SaveImageAsync(1000);
        var (_, newest) = await SaveImageAsync(1000);
        var recorder = new RecordingClient();
        var messages = new[] { User("1", oldest), User("2", middle), User("3", newest) };

        await new AttachmentInlineChatClient(recorder, Inliner(perImage: 1024, total: 2100)).GetResponseAsync(messages);

        Assert.IsType<TextContent>(recorder.Seen[0].Contents[1]);
        Assert.IsType<DataContent>(recorder.Seen[1].Contents[1]);
        Assert.IsType<DataContent>(recorder.Seen[2].Contents[1]);
    }

    [Fact]
    public async Task AnAttachmentThatIsGone_BecomesAPlaceholder()
    {
        var ghost = new AttachmentRef("ghost", "u/c/ghost", "image/png", "g.png");
        var image = new UriContent(await _store.GetReadUrlAsync(ghost), "image/png");
        var recorder = new RecordingClient();

        await new AttachmentInlineChatClient(recorder, Inliner()).GetResponseAsync([User("x", image)]);

        Assert.Equal(AttachmentInliner.Placeholder, Assert.IsType<TextContent>(Assert.Single(recorder.Seen).Contents[1]).Text);
    }

    [Fact]
    public async Task OtherUrls_AndPlainText_PassThroughAsTheSameInstances()
    {
        var web = new UriContent("https://example.com/a.png", "image/png");
        var messages = new[] { User("plain"), User("web", web) };

        var result = await Inliner().InlineAsync(messages, CancellationToken.None);

        Assert.Same(messages, result);
    }

    [Fact]
    public async Task EveryProviderKind_TheFactoryBuilds_RewritesTheReference()
    {
        var kinds = Enum.GetValues<ProviderKind>();
        Assert.NotEmpty(kinds);

        foreach (var kind in kinds)
        {
            var (_, image) = await SaveImageAsync(3);
            var recorder = new RecordingClient();
            var providers = new Dictionary<string, MultiProviderChatClientFactory.Provider>
            {
                ["p"] = new(new ProviderOptions { Kind = kind }, _ => recorder),
            };
            var factory = new MultiProviderChatClientFactory(
                providers, "p:m", "test", NullLogger<MultiProviderChatClientFactory>.Instance, attachmentInliner: Inliner());

            await factory.GetClient("p:m").GetResponseAsync([User("x", image)]);

            var sent = Assert.Single(recorder.Seen);
            Assert.True(sent.Contents[1] is DataContent, $"{kind}: the provider's client saw {sent.Contents[1].GetType().Name}, not image bytes");
        }
    }

    [Fact]
    public async Task WithoutAnInliner_TheFactoryLeavesTheReferenceAlone()
    {
        var (_, image) = await SaveImageAsync(3);
        var recorder = new RecordingClient();
        var providers = new Dictionary<string, MultiProviderChatClientFactory.Provider>
        {
            ["p"] = new(new ProviderOptions { Kind = ProviderKind.OpenAICompatible }, _ => recorder),
        };
        var factory = new MultiProviderChatClientFactory(providers, "p:m", "test", NullLogger<MultiProviderChatClientFactory>.Instance);

        await factory.GetClient("p:m").GetResponseAsync([User("x", image)]);

        Assert.Same(image, Assert.Single(recorder.Seen).Contents[1]);
    }
}
