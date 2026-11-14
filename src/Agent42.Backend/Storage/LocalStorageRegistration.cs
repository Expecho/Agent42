using Agent42.Agents;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Agent42.Backend;

/// <summary>
/// The local provider (<c>Storage:Provider = local</c>): Postgres plus a filesystem volume, so the app runs with no Azure
/// account. Grown step by step under the self-hosting plan; once the plan's storage steps are done it registers every
/// port the Azure provider does.
/// </summary>
public static class LocalStorageRegistration
{
    /// <summary>The root every file-backed store lives under (the volume mounted into the container).</summary>
    public const string RootKey = "Storage:Local:Root";

    public static void AddLocalStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var root = configuration[RootKey];
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"{RootKey} is not configured. Storage:Provider is 'local', so set it to the directory the file stores " +
                "write under (the Storage__Local__Root environment variable, e.g. /data).");
        }

        services.AddFileStores(root, configuration);
        services.AddPostgres(configuration);
    }

    /// <summary>
    /// The Postgres data source (<c>ConnectionStrings:postgres</c>, with pgvector's type mapping), and the schema bootstrap
    /// that runs before the silo starts. Registered before the Orleans silo, which is what orders the two.
    /// </summary>
    public static void AddPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("postgres");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:postgres is not configured. Storage:Provider is 'local', so set it to the Postgres " +
                "connection string (the ConnectionStrings__postgres environment variable).");
        }

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connection);
            builder.UseVector();
            return builder.Build();
        });
        var dimensions = configuration.GetValue("Storage:Local:DocChunksDimensions", PostgresSchema.DefaultDocChunkDimensions);
        services.AddSingleton(new LocalPostgresOptions(dimensions));
        services.AddHostedService<PostgresSchemaInitializer>();

        // The document index (ADR 0007) and the chat search index (ADR 0061) on Postgres, in place of Cosmos.
        services.AddSingleton<IDocumentIndex>(sp => new PostgresDocumentIndex(sp.GetRequiredService<NpgsqlDataSource>(), dimensions));
        services.AddSingleton<IChatSearchIndex, PostgresChatSearchIndex>();
        // Registered before AddChatGateway, whose TryAdd of the Blob lease then leaves this one in place (ADR 0111, 0127).
        services.AddSingleton<IChatGatewayLease, PostgresChatGatewayLease>();
    }

    /// <summary>
    /// Every blob-shaped port on the filesystem, plus the Data Protection key ring beside them. The ring MUST persist
    /// (ADR 0018): an ephemeral one rotates on restart and orphans every stored provider secret.
    /// </summary>
    public static void AddFileStores(this IServiceCollection services, string root, IConfiguration configuration)
    {
        var files = new LocalFileStore(root);
        services.AddSingleton(files);
        services.AddSingleton<IAttachmentStore, FileAttachmentStore>();
        services.AddSingleton<ISkillPackageStore, FileSkillPackageStore>();
        services.AddSingleton<IFleetBlobs, FileFleetBlobs>();
        services.AddSingleton<ICopilotSessionStore, FileCopilotSessionStore>();

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(files.Root, "dataprotection")));

        // The attachment reference URLs FileAttachmentStore hands out are swapped for image bytes at request time.
        var defaults = new AttachmentInlineOptions();
        services.AddSingleton(new AttachmentInlineOptions(
            configuration.GetValue("Storage:Local:InlineImageMaxBytes", defaults.PerImageBytes),
            configuration.GetValue("Storage:Local:InlineImagesTotalBytes", defaults.TotalBytes)));
        services.AddSingleton<AttachmentInliner>();
    }

    /// <summary>
    /// Grain state and reminders on Postgres through Orleans' ADO.NET providers. The serializer is left at the provider's
    /// default, which a spike showed is the same <c>$type</c>/<c>$id</c> JSON the Cosmos path writes (ADR 0039, 0127).
    /// </summary>
    public static void UseLocalStorage(this ISiloBuilder silo, IConfiguration configuration)
    {
        silo.Services.AddAdoNetGrainStorage(AgentGrain.StorageProviderName, options =>
            options.Configure<NpgsqlDataSource>((o, dataSource) =>
            {
                o.Invariant = "Npgsql";
                o.DataSource = dataSource;
            }));
        silo.UseAdoNetReminderService(options =>
            options.Configure<NpgsqlDataSource>((o, dataSource) =>
            {
                o.Invariant = "Npgsql";
                o.DataSource = dataSource;
            }));
    }
}
