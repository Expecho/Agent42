using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;
using Xunit.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// Runs only when a Foundry key is configured — xunit 2 has no runtime skip, so the attribute decides.
/// </summary>
public sealed class LiveModelFactAttribute : FactAttribute
{
    public LiveModelFactAttribute()
    {
        if (string.IsNullOrEmpty(LiveModelSettings.Key))
        {
            Skip = $"Put {{\"key\": \"...\"}} in tests/Agent42.Tests/{LiveModelSettings.LocalFileName} (git-ignored), "
                + $"or set {LiveModelSettings.KeyVariable}, to run against a real Foundry model.";
        }
    }
}

/// <summary>
/// Where the live test's model lives. Read from the git-ignored <see cref="LocalFileName"/> next to this
/// project (<c>key</c>, optional <c>endpoint</c> and <c>model</c>); an environment variable wins over the file.
/// The key is never committed. The endpoint is the Foundry RESOURCE, not a project URL.
/// </summary>
public static class LiveModelSettings
{
    public const string LocalFileName = "live-model.local.json";
    public const string KeyVariable = "AGENT42_LIVE_FOUNDRY_KEY";
    public const string EndpointVariable = "AGENT42_LIVE_FOUNDRY_ENDPOINT";
    public const string ModelVariable = "AGENT42_LIVE_MODEL";
    public const string Provider = "foundry";

    private static readonly Lazy<JsonElement?> LocalFile = new(ReadLocalFile);

    // The provider appends /openai/v1 to this, so it is the resource root — the same value as
    // AzureOpenAI:Endpoint in appsettings.json — not the /api/projects/... project endpoint.
    public static Uri Endpoint => new(Setting(EndpointVariable, "endpoint") ?? "https://agent42.services.ai.azure.com");

    public static string Model => Setting(ModelVariable, "model") ?? "gpt-5-mini";

    public static string Key => Setting(KeyVariable, "key") ?? "";

    private static string? Setting(string variable, string property) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnv
            ? fromEnv
            : LocalFile.Value is { } file && file.TryGetProperty(property, out var value) ? value.GetString() : null;

    // The test runs from bin/<config>/<tfm>, so walk up to the project directory that holds the file.
    private static JsonElement? ReadLocalFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, LocalFileName);
            if (File.Exists(path))
            {
                return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            }
        }

        return null;
    }
}

/// <summary>
/// The in-memory store, except the model gets the image as a <c>data:</c> URL. The in-memory store's
/// <c>inmemory://</c> placeholder is fine for fakes that never fetch it, but a real model must be able to
/// read the bytes, and there is no blob container to mint a SAS into.
/// </summary>
public sealed class DataUrlAttachmentStore : IAttachmentStore
{
    private readonly InMemoryAttachmentStore _inner = new();

    public Task<AttachmentRef> SaveAsync(
        string ownerKey, string fileName, string mediaType, byte[] content, CancellationToken cancellationToken = default)
        => _inner.SaveAsync(ownerKey, fileName, mediaType, content, cancellationToken);

    public Task<AttachmentRef?> GetAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(ownerKey, attachmentId, cancellationToken);

    public Task<AttachmentContent?> OpenReadAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
        => _inner.OpenReadAsync(ownerKey, attachmentId, cancellationToken);

    public Task<AttachmentRef> CopyAsync(AttachmentRef source, string targetOwnerKey, CancellationToken cancellationToken = default)
        => _inner.CopyAsync(source, targetOwnerKey, cancellationToken);

    public async Task<Uri> GetReadUrlAsync(AttachmentRef reference, CancellationToken cancellationToken = default)
    {
        var ownerKey = reference.BlobPath[..reference.BlobPath.LastIndexOf('/')];
        var content = await _inner.OpenReadAsync(ownerKey, reference.Id, cancellationToken)
            ?? throw new InvalidOperationException($"attachment {reference.Id} is not in the store");
        return new Uri($"data:{content.MediaType};base64,{Convert.ToBase64String(content.Bytes)}");
    }
}

/// <summary>
/// A silo whose chat factory is the REAL <see cref="MultiProviderChatClientFactory"/> with one Foundry provider on
/// the Responses API — the shape production runs (ADR 0014/0017). Everything else is the offline stand-ins.
/// </summary>
public sealed class LiveModelSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            var options = new ProviderOptions
            {
                Kind = ProviderKind.AzureOpenAI,
                UseResponsesApi = true,
                Models = [new ConfiguredModel { Id = LiveModelSettings.Model, SupportsTools = true, SupportsVision = true }],
            };
            var provider = MultiProviderChatClientFactory.Provider.Foundry(options, LiveModelSettings.Endpoint, LiveModelSettings.Key);
            services.AddSingleton<IChatClientFactory>(new MultiProviderChatClientFactory(
                new Dictionary<string, MultiProviderChatClientFactory.Provider> { [LiveModelSettings.Provider] = provider },
                ModelId.Qualify(LiveModelSettings.Provider, LiveModelSettings.Model),
                "Agent42.Tests.Live",
                NullLogger<MultiProviderChatClientFactory>.Instance));
            services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
                new SingleUserChatClientFactoryProvider(sp.GetRequiredService<IChatClientFactory>()));
            services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
            services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();
            services.AddSingleton<IUserSkillProvider, UserSkillProvider>();
            services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
            services.AddSingleton<IAgentWebhookSender, NullAgentWebhookSender>();
            services.AddSingleton<IChatDeliverySender, NullChatDeliverySender>();
            services.AddSingleton<IFleetLauncher, DisabledFleetLauncher>();
            // Mandatory grain dependency; null client = no long-term memory, so no Hindsight egress.
            services.AddSingleton(new HindsightIntegration(null));
            services.AddSingleton<IAttachmentStore, DataUrlAttachmentStore>();
            services.AddDocumentRagFakes();
            services.AddWebToolFakes();

            // Last: supplies the host-invariant services and fails the silo at startup, naming the type, if a grain
            // dependency above is missing (ADR 0105).
            services.AddAgent42Silo();
        });
    }
}

/// <summary>
/// End to end against a REAL model: upload an image through <c>/api/conversations/{id}/attachments</c>, chat
/// about it through <c>/api/chat/{id}</c>, and read the NDJSON stream — endpoint → grain → Foundry and back.
/// Opt-in (costs tokens, needs network and a key), and cluster-per-test because it brings its own configurator
/// (ADR 0055's exception, like <c>AttachmentRehydrateTests</c>).
/// </summary>
public sealed class LiveModelChatTests(ITestOutputHelper output)
{
    [LiveModelFact]
    public async Task RealModel_DescribesAnUploadedImage()
    {
        var builder = new TestClusterBuilder { Options = { InitialSilosCount = 1 } };
        builder.AddSiloBuilderConfigurator<LiveModelSiloConfigurator>();
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Services.AddSingleton<IGrainFactory>(cluster.GrainFactory);
        SharedApiFixture.AddRouteMatcherServices(appBuilder.Services);
        await using var app = appBuilder.Build();
        app.MapAgentApi();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.Timeout = TimeSpan.FromMinutes(3);

        var id = $"live-image-{Guid.NewGuid():n}";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(RedCircleOnWhitePng(128));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "shape.png");
        var upload = await client.PostAsync($"/api/conversations/{id}/attachments", form);
        upload.EnsureSuccessStatusCode();
        var metadata = await upload.Content.ReadFromJsonAsync<AttachmentMetadata>();

        using var chat = await client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest(
            "What is visible on this image? Name the shape and its color in one short sentence.",
            ModelId.Qualify(LiveModelSettings.Provider, LiveModelSettings.Model), null, [metadata!.Id]));
        chat.EnsureSuccessStatusCode();
        var events = (await chat.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var reply = string.Concat(events
            .Where(e => e.GetProperty("type").GetString() == "textDelta")
            .Select(e => e.GetProperty("text").GetString()));
        output.WriteLine($"{LiveModelSettings.Model}: {reply}");
        foreach (var other in events.Where(e => e.GetProperty("type").GetString() is not ("textDelta" or "reasoning")))
        {
            output.WriteLine(other.GetRawText());
        }

        Assert.Contains(events, e => e.GetProperty("type").GetString() == "done");
        Assert.Contains("red", reply, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            reply.Contains("circle", StringComparison.OrdinalIgnoreCase) || reply.Contains("disc", StringComparison.OrdinalIgnoreCase),
            $"expected the reply to name a circle: {reply}");

        // The turn persisted with its attachment, so a reopened conversation re-renders the image.
        var turns = await client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages");
        var userTurn = Assert.Single(turns!, t => t.Role == "user");
        Assert.Equal(metadata.Id, Assert.Single(userTurn.Attachments!).Id);
        Assert.Contains(turns!, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    /// <summary>A size×size RGB PNG: a red filled circle on white. Built here so the test needs no binary fixture.</summary>
    private static byte[] RedCircleOnWhitePng(int size)
    {
        var raw = new byte[size * ((size * 3) + 1)];
        var center = (size - 1) / 2.0;
        var radius = size * 0.35;
        for (var y = 0; y < size; y++)
        {
            var row = y * ((size * 3) + 1); // leading filter byte 0 = None
            for (var x = 0; x < size; x++)
            {
                var inside = Math.Pow(x - center, 2) + Math.Pow(y - center, 2) <= radius * radius;
                raw[row + 1 + (x * 3)] = 255;
                raw[row + 2 + (x * 3)] = inside ? (byte)0 : (byte)255;
                raw[row + 3 + (x * 3)] = inside ? (byte)0 : (byte)255;
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)size);
        WriteBigEndian(header, 4, (uint)size);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: truecolour RGB
        WriteChunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            WriteChunk(png, "IDAT", compressed.ToArray());
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(typed));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
