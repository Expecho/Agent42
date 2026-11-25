namespace Agent42.Backend;

/// <summary>
/// The facts about <c>Auth:Mode=entra</c> that must not fail silently (ADR 0145), as pure functions of configuration so the
/// startup warning and the config-health rows (ADR 0029) cannot disagree.
/// </summary>
public static class AuthStartupChecks
{
    public const string DataProtectionWarning =
        "Auth:Mode is 'entra' but the Data Protection key ring is not persisted: every restart signs everyone out AND orphans " +
        "stored provider API keys and chat-bot credentials. On the Azure path set Storage:BlobEndpoint (not " +
        "ConnectionStrings:blobs) and give the service principal Storage Blob Data Contributor.";

    public const string LocalDevOwnerWarning =
        "Access:Owners lists 'local-dev'. With real sign-in nobody is local-dev, and the entry would make every unauthenticated " +
        "public-path request an owner. Remove it (ADR 0062/0121).";

    /// <summary>
    /// Whether the Data Protection key ring survives a restart. The local provider writes it to the volume
    /// (<c>LocalStorageRegistration</c>); the Azure provider persists it to Blob only on the endpoint path, never with a
    /// connection string (<c>AzureStorageRegistration</c>).
    /// </summary>
    public static bool DataProtectionRingPersisted(IConfiguration configuration)
        => StorageProviderSetting.Read(configuration) == StorageProvider.Local
           || (string.IsNullOrWhiteSpace(configuration.GetConnectionString("blobs"))
               && !string.IsNullOrWhiteSpace(configuration["Storage:BlobEndpoint"]));

    public static bool OwnersContainLocalDev(IConfiguration configuration)
        => (configuration.GetSection(OwnerPolicy.SectionName).Get<string[]>() ?? [])
            .Any(id => string.Equals(id?.Trim(), CurrentUser.LocalDevelopmentUserId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Logs the entra-mode warnings once at startup. Warning, not Information: production logs at Warning (ADR 0054). Returns how many it logged.</summary>
    public static int LogIfEntra(IConfiguration configuration, ILogger logger)
    {
        if (AuthModeSetting.Read(configuration) != AuthMode.Entra)
        {
            return 0;
        }

        var logged = 0;
        if (!DataProtectionRingPersisted(configuration))
        {
            logger.LogWarning(DataProtectionWarning);
            logged++;
        }

        if (OwnersContainLocalDev(configuration))
        {
            logger.LogWarning(LocalDevOwnerWarning);
            logged++;
        }

        return logged;
    }
}
