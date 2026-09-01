using Agent42.Agents;
using Microsoft.Azure.Cosmos;

namespace Agent42.Backend;

/// <summary>
/// Registers the chat search index (ADR 0061) — the one startup decision the Search feature owns.
///
/// <para>It lives in the feature folder rather than <c>Program.cs</c> for the reason ADR 0050
/// established and <see cref="DocumentIndexRegistration"/> records: the composition root is a long
/// run of configuration branches already near <c>cognitive_complexity_max</c>, and lifting a
/// feature's registration into that feature's folder is a better answer than reshaping a branch in
/// place to shed a point.</para>
/// </summary>
public static class ChatSearchIndexRegistration
{
    /// <summary>
    /// Uses Cosmos only when the chat-messages container is configured (production, threaded from
    /// Bicep). Absent that key — DevHost, the AppHost's Cosmos emulator, tests — the in-memory twin
    /// is registered, so nothing offline is affected and the emulator never receives a query against
    /// a container that does not exist there. The container is pre-created in Bicep;
    /// <see cref="CosmosChatSearchIndex"/> never creates on use (<c>disableLocalAuth</c>).
    /// </summary>
    public static void AddChatSearchIndex(this IServiceCollection services, IConfiguration configuration)
    {
        var chatMessagesContainer = configuration["Cosmos:ChatMessagesContainerName"];
        if (string.IsNullOrWhiteSpace(chatMessagesContainer))
        {
            services.AddSingleton<IChatSearchIndex, InMemoryChatSearchIndex>();
            return;
        }

        services.AddSingleton<IChatSearchIndex>(sp => new CosmosChatSearchIndex(
            sp.GetRequiredService<CosmosClient>(), "agent42", chatMessagesContainer));
    }
}
