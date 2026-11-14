using Agent42.Agents;
using Agent42.Backend;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent42.Tests;

/// <summary>
/// The <c>Storage:Provider</c> switch (self-hosting plan, Step 2). Nothing in the suite runs <c>Program.cs</c>, so
/// the choice lives in <see cref="StorageRegistration"/> and is tested here on its own.
/// </summary>
public sealed class StorageProviderOptionsTests
{
    private const string EmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    [Theory]
    [InlineData(null, StorageProvider.Azure)]
    [InlineData("", StorageProvider.Azure)]
    [InlineData("   ", StorageProvider.Azure)]
    [InlineData("azure", StorageProvider.Azure)]
    [InlineData("AZURE", StorageProvider.Azure)]
    [InlineData("local", StorageProvider.Local)]
    [InlineData(" Local ", StorageProvider.Local)]
    public void Provider_AbsentMeansAzure_AndKnownValuesParse(string? value, StorageProvider expected)
    {
        Assert.Equal(expected, StorageProviderSetting.Read(Config((StorageProviderSetting.Key, value))));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("loca1")]
    [InlineData("s3")]
    public void UnknownProvider_Throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => StorageProviderSetting.Read(Config((StorageProviderSetting.Key, value))));
        Assert.Contains(value, ex.Message);
        Assert.Contains("azure", ex.Message);
    }

    [Fact]
    public void UnknownProvider_FailsTheHostRegistration_NotJustTheParser()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [StorageProviderSetting.Key] = "mongo" });

        Assert.Throws<InvalidOperationException>(() => builder.AddStorageProvider(new DefaultAzureCredential()));
    }

    /// <summary>AC2: with the key absent, the Azure provider registers the same ports Program.cs registered inline.</summary>
    [Fact]
    public void AzureProvider_RegistersTheSamePorts()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:cosmos"] = $"AccountEndpoint=https://localhost:8081/;AccountKey={EmulatorKey}",
            ["Storage:BlobEndpoint"] = "https://example.blob.core.windows.net",
        });

        builder.AddStorageProvider(new DefaultAzureCredential());

        using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<BlobAttachmentStore>(provider.GetRequiredService<IAttachmentStore>());
        Assert.IsType<BlobSkillPackageStore>(provider.GetRequiredService<ISkillPackageStore>());
        Assert.IsType<InMemoryDocumentIndex>(provider.GetRequiredService<IDocumentIndex>());
        Assert.IsType<InMemoryChatSearchIndex>(provider.GetRequiredService<IChatSearchIndex>());
        Assert.NotNull(provider.GetRequiredService<CosmosClient>());
    }

    [Fact]
    public void AzureProvider_WithoutABlobEndpoint_StillFailsAtStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:cosmos"] = $"AccountEndpoint=https://localhost:8081/;AccountKey={EmulatorKey}",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddStorageProvider(new DefaultAzureCredential()));
        Assert.Contains("Storage:BlobEndpoint", ex.Message);
    }

    [Fact]
    public void FleetAndCopilotRegistrations_LeaveAnEarlierStoreInPlace()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFleetBlobs, InMemoryFleetBlobs>();
        services.AddSingleton<ICopilotSessionStore, InMemoryCopilotSessionStore>();
        var configuration = Config();

        services.AddCopilotAgents(configuration);
        services.AddAgentFleet(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryFleetBlobs>(provider.GetRequiredService<IFleetBlobs>());
        Assert.IsType<InMemoryCopilotSessionStore>(provider.GetRequiredService<ICopilotSessionStore>());
    }

    /// <summary>AC16: `local` registers every port with no Azure client and no cosmos/blobs connection string.</summary>
    [Fact]
    public void Local_ResolvesEveryPort_WithoutAzure()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent42-ports-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [StorageProviderSetting.Key] = "local",
                ["Storage:Local:Root"] = root,
                ["ConnectionStrings:postgres"] = "Host=localhost;Database=none;Username=none;Password=none",
            });

            // The order Program.cs uses: the storage provider first, then the features that TryAdd an Azure default.
            builder.AddStorageProvider(new DefaultAzureCredential());
            builder.Services.AddCopilotAgents(builder.Configuration);
            builder.Services.AddAgentFleet(builder.Configuration);
            builder.Services.AddChatGateway();

            Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(CosmosClient) || d.ServiceType == typeof(BlobServiceClient));
            Assert.Null(builder.Configuration.GetConnectionString("cosmos"));
            Assert.Null(builder.Configuration.GetConnectionString("blobs"));

            using var provider = builder.Services.BuildServiceProvider();
            Assert.IsType<FileAttachmentStore>(provider.GetRequiredService<IAttachmentStore>());
            Assert.IsType<FileSkillPackageStore>(provider.GetRequiredService<ISkillPackageStore>());
            Assert.IsType<FileFleetBlobs>(provider.GetRequiredService<IFleetBlobs>());
            Assert.IsType<FileCopilotSessionStore>(provider.GetRequiredService<ICopilotSessionStore>());
            Assert.IsType<PostgresDocumentIndex>(provider.GetRequiredService<IDocumentIndex>());
            Assert.IsType<PostgresChatSearchIndex>(provider.GetRequiredService<IChatSearchIndex>());
            Assert.IsType<PostgresChatGatewayLease>(provider.GetRequiredService<IChatGatewayLease>());
            Assert.NotNull(provider.GetRequiredService<AttachmentInliner>());
            Assert.Equal(PostgresSchema.DefaultDocChunkDimensions, provider.GetRequiredService<IDocumentIndex>().ExpectedDimensions);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Storage:Local:Root", "ConnectionStrings:postgres")]
    [InlineData("ConnectionStrings:postgres", "Storage:Local:Root")]
    public void Local_WithoutItsOwnSettings_FailsAtStartupNamingTheKey(string present, string missing)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StorageProviderSetting.Key] = "local",
            [present] = present.StartsWith("Storage", StringComparison.Ordinal) ? Path.GetTempPath() : "Host=h",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddStorageProvider(new DefaultAzureCredential()));
        Assert.Contains(missing.Replace("ConnectionStrings:", ""), ex.Message);
    }
}
