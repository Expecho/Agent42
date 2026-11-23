using Azure.Identity;

namespace Agent42.Backend;

/// <summary>
/// The <see cref="DefaultAzureCredential"/> options built from <c>AzureOpenAI:TenantId</c>. <c>appsettings.json</c> ships that
/// value as <c>""</c>, and Azure.Identity rejects an empty tenant id with <c>ArgumentException: Invalid tenant id provided</c>
/// (only <c>null</c> means "not set"), so a host that does not set it (the AppHost, a hand-started exe) crashed at startup.
/// Prod sets it from the deploy; an empty or whitespace value is treated as unset.
/// </summary>
public static class AzureCredentialSettings
{
    public static DefaultAzureCredentialOptions Options(IConfiguration configuration) => new()
    {
        TenantId = string.IsNullOrWhiteSpace(configuration["AzureOpenAI:TenantId"]) ? null : configuration["AzureOpenAI:TenantId"],
    };
}
