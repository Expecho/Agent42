namespace Agent42.Backend;

/// <summary>Where the app keeps its durable state: today's Azure services, or Postgres plus a filesystem volume.</summary>
public enum StorageProvider
{
    /// <summary>Cosmos DB and Blob Storage. The default, and what production runs.</summary>
    Azure,

    /// <summary>Postgres (grain state, reminders, vectors, chat search, the gateway lease) and a filesystem volume.</summary>
    Local,
}

/// <summary>
/// Reads <c>Storage:Provider</c>. Absent means <see cref="StorageProvider.Azure"/>, so production's Bicep needs no
/// change; a value that is present but unknown fails startup, because silently falling back to Azure on a typo would
/// point a self-hoster's data at a Cosmos account they do not have.
/// </summary>
public static class StorageProviderSetting
{
    public const string Key = "Storage:Provider";

    public static StorageProvider Read(IConfiguration configuration)
    {
        var value = configuration[Key]?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("azure", StringComparison.OrdinalIgnoreCase))
        {
            return StorageProvider.Azure;
        }

        if (value.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return StorageProvider.Local;
        }

        throw new InvalidOperationException($"{Key} is '{value}'. Use 'azure' or 'local'.");
    }
}
