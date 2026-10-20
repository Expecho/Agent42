using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Registers the copilot agent kind's runtime host: the Blob session store and the Copilot SDK agent factory. Lifted
/// out of <c>Program.cs</c> the way <see cref="HindsightRegistration"/> was (ADR 0050, rule 9).
///
/// <para>The grain resolves <see cref="ICopilotAgentFactory"/> optionally, so a host without this still activates
/// every grain and only refuses copilot turns. DevHost and the test silo register the in-process twin instead.</para>
/// </summary>
public static class CopilotRegistration
{
    public static void AddCopilotAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ICopilotSessionStore, BlobCopilotSessionStore>();
        services.AddSingleton(new CopilotRuntimeOptions
        {
            RuntimePath = configuration["Copilot:RuntimePath"],
            SessionIdleTimeoutSeconds = configuration.GetValue("Copilot:SessionIdleTimeoutSeconds", 1800),
            BuiltInToolsEnabled = configuration.GetValue("Copilot:BuiltInToolsEnabled", true),
            WorkspaceRoot = configuration["Copilot:WorkspaceRoot"] is { Length: > 0 } root ? root : new CopilotRuntimeOptions().WorkspaceRoot,
        });
        // By type, so the container disposes it (and with it the runtime process) on shutdown.
        services.AddSingleton<CopilotAgentFactory>();
        services.AddSingleton<ICopilotAgentFactory>(sp => sp.GetRequiredService<CopilotAgentFactory>());
        // GitHub Copilot model discovery (ADR 0108) rides the same runtime; UserModelCatalogProvider takes it optionally.
        services.AddSingleton<ICopilotModelLister>(sp => sp.GetRequiredService<CopilotAgentFactory>());
        // "Sign in with GitHub" (ADR 0108). Always registered here; blank options make it answer 503. A plain HttpClient,
        // so no AddServiceDefaults retry pipeline (rule 8).
        services.AddSingleton(new GitHubOAuthClient(new HttpClient(), new GitHubOAuthOptions
        {
            ClientId = configuration["GitHub:OAuth:ClientId"] ?? "",
            ClientSecret = configuration["GitHub:OAuth:ClientSecret"] ?? "",
            Scopes = configuration["GitHub:OAuth:Scopes"] ?? "",
        }));
        services.AddSingleton(sp => new CopilotModelDirectory(
            sp.GetRequiredService<ICopilotModelLister>(), TimeProvider.System,
            sp.GetRequiredService<ILogger<CopilotModelDirectory>>()));
    }
}
