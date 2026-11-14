using Azure.Core;
using Orleans.Hosting;

namespace Agent42.Backend;

/// <summary>
/// The one place the Backend chooses between Azure and local storage (<c>Storage:Provider</c>). Each provider's wiring is
/// its own static class so the composition root stays free of provider branches (S3776, ADR 0050).
///
/// <para>Called by <c>Program.cs</c> BEFORE <c>AddAgent42Silo()</c> (pinned by
/// <c>ProjectLayoutTests.Hosts_CallAddAgent42Silo</c>), because that call validates the ports already registered.</para>
/// </summary>
public static class StorageRegistration
{
    /// <summary>
    /// Registers the blob-side and index ports for the chosen provider, plus the provider's own clients. The credential
    /// is only used by the Azure provider (managed identity), and is lazy: nothing touches the network at registration.
    /// </summary>
    public static void AddStorageProvider(this IHostApplicationBuilder builder, TokenCredential credential)
    {
        if (StorageProviderSetting.Read(builder.Configuration) == StorageProvider.Local)
        {
            builder.Services.AddLocalStorage(builder.Configuration);
        }
        else
        {
            builder.AddAzureStorage(credential);
        }
    }

    /// <summary>Configures Orleans grain storage and reminders for the chosen provider.</summary>
    public static void UseStorageProvider(this ISiloBuilder silo, IConfiguration configuration)
    {
        if (StorageProviderSetting.Read(configuration) == StorageProvider.Local)
        {
            silo.UseLocalStorage(configuration);
        }
        else
        {
            silo.UseAzureStorage();
        }
    }
}
