using Agent42.Agents;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Cosmos;
using Orleans.Hosting;

namespace Agent42.Backend;

/// <summary>
/// The Azure provider (<c>Storage:Provider = azure</c>, the default): Cosmos DB and Blob Storage. This is the wiring
/// <c>Program.cs</c> carried inline before the provider switch, moved here unchanged in registrations and order.
/// </summary>
public static class AzureStorageRegistration
{
    /// <summary>The Cosmos client (Aspire-provided "cosmos" connection), then the Blob-side and index ports.</summary>
    public static void AddAzureStorage(this IHostApplicationBuilder builder, TokenCredential credential)
    {
        builder.AddAzureCosmosClient("cosmos");
        builder.Services.AddAzureBlobStorage(builder.Configuration, credential);
        builder.Services.AddDocumentIndex(builder.Configuration);
        builder.Services.AddChatSearchIndex(builder.Configuration);
    }

    /// <summary>
    /// Blob-backed attachment store (ADR 0004): durable image bytes live in blob storage, never in the serialized
    /// session. Prefer an Aspire-provided connection (Azurite under the AppHost); otherwise use the blob endpoint + the
    /// same managed identity, whose Storage Blob Data Contributor role also enables key-free user-delegation SAS.
    /// </summary>
    public static void AddAzureBlobStorage(this IServiceCollection services, IConfiguration configuration, TokenCredential credential)
    {
        var blobConnection = configuration.GetConnectionString("blobs");
        if (!string.IsNullOrWhiteSpace(blobConnection))
        {
            services.AddSingleton(new BlobServiceClient(blobConnection));
        }
        else
        {
            var blobEndpoint = configuration["Storage:BlobEndpoint"]
                ?? throw new InvalidOperationException(
                    "Storage:BlobEndpoint is not configured. Set it in appsettings.json, user secrets, " +
                    "or the Storage__BlobEndpoint environment variable (e.g. https://<account>.blob.core.windows.net).");
            services.AddSingleton(new BlobServiceClient(new Uri(blobEndpoint), credential));
        }

        services.AddSingleton<IAttachmentStore, BlobAttachmentStore>();

        // Data Protection (ADR 0018): encrypts the API keys of user-configured providers. In prod the key ring MUST be
        // persisted out-of-process — an ephemeral ring rotates on restart and orphans every stored secret. Gate on the
        // PROD branch (Storage:BlobEndpoint set, and NOT the Azurite/AppHost GetConnectionString("blobs") path) and use
        // the MI PersistKeysToAzureBlobStorage(BlobClient) overload: shared-key auth is disabled on the account, so the
        // connection-string overload cannot work. The master key itself is protected only by Storage SSE + RBAC (no
        // ProtectKeysWith…) — the user secrets it encrypts are DP-encrypted on top.
        var dataProtection = services.AddDataProtection();
        var dataProtectionBlobEndpoint = configuration["Storage:BlobEndpoint"];
        if (string.IsNullOrWhiteSpace(blobConnection) && !string.IsNullOrWhiteSpace(dataProtectionBlobEndpoint))
        {
            var keyRingBlob = new BlobClient(
                new Uri(dataProtectionBlobEndpoint.TrimEnd('/') + "/dataprotection/keys.xml"), credential);
            dataProtection.PersistKeysToAzureBlobStorage(keyRingBlob);
        }

        // Uploaded agent skills (ADR 0049): the Blob package store. Resolved only OPTIONALLY, out of IServiceProvider
        // inside the endpoint handler, so a host that omits it degrades to a 409 instead of breaking route matching for
        // the whole host. The IUserSkillProvider over it is a grain dependency and comes from AddAgent42Silo.
        services.AddSingleton<ISkillPackageStore, BlobSkillPackageStore>();
    }

    /// <summary>
    /// Orleans grain state in the Cosmos "agent-state" container, and durable reminders (the schedule_prompt tool) in
    /// "reminders". Resource creation is disabled: the account has disableLocalAuth: true, and Cosmos's AAD data-plane
    /// RBAC cannot create databases/containers (only ARM control-plane can), so both are pre-created in
    /// infra/modules/cosmos.bicep.
    /// </summary>
    public static void UseAzureStorage(this ISiloBuilder silo)
    {
        silo.AddCosmosGrainStorage(AgentGrain.StorageProviderName, options =>
        {
            options.ConfigureCosmosClient(sp =>
                ValueTask.FromResult(sp.GetRequiredService<CosmosClient>()));
            options.DatabaseName = "agent42";
            options.ContainerName = "agent-state";
            options.IsResourceCreationEnabled = false;
        });

        silo.UseCosmosReminderService(options =>
        {
            options.ConfigureCosmosClient(sp =>
                ValueTask.FromResult(sp.GetRequiredService<CosmosClient>()));
            options.DatabaseName = "agent42";
            options.ContainerName = "reminders";
            options.IsResourceCreationEnabled = false;
        });
    }
}
