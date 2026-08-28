using Agent42.Agents;
using Hindsight.AgentFramework;

namespace Agent42.Backend;

/// <summary>
/// Registers long-term memory (ADR 0056) — the one startup decision the Memory feature owns.
///
/// <para>Lifted out of <c>Program.cs</c> for the reason ADR 0050 records and
/// <see cref="DocumentIndexRegistration"/> set the precedent for: adding this feature's
/// <c>if</c>/<c>else</c> in place took the composition root from 15 to 16 and failed the build on
/// S3776. Extracting a feature's registration into that feature's folder is the ADR 0040 shape
/// applied to startup, and a better answer than reshaping the branch to shed a point.</para>
/// </summary>
public static class HindsightRegistration
{
    /// <summary>
    /// Wires the Hindsight memory bank when a key is configured, and wires memory OFF when it is not.
    ///
    /// <para><b>Optional, and off by default</b> — the <c>GOOGLE_MAPS_API_KEY</c> shape, not
    /// <c>SEARXNG_SECRET</c>'s: with no key the app boots normally. The null <c>Client</c> is not a
    /// degraded client but an instruction to <c>BuildAgent</c> to attach no provider at all, so a turn
    /// makes <em>zero</em> Hindsight calls rather than calls that quietly fail. A
    /// <see cref="HindsightIntegration"/> is registered either way, because it is a mandatory grain
    /// dependency: omitting it fails activation rather than disabling memory.</para>
    ///
    /// <para><b>Not setting the key is also the only off switch.</b> There is no Preferences toggle and
    /// no <c>ToolCatalog</c> key (out of scope, ADR 0056), so once configured this runs on every turn of
    /// every non-incognito conversation, sending the user's message and the assistant's reply to a third
    /// party. Read ADR 0056's consequences before widening this.</para>
    /// </summary>
    public static void AddHindsightMemory(this IServiceCollection services, IConfiguration configuration)
    {
        var apiKey = configuration["Hindsight:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton(new HindsightIntegration(null));
            return;
        }

        // IsNullOrWhiteSpace, not ?? — an unset optional value threads through Bicep and the env as an
        // EMPTY STRING, not as null, and `new Uri("")` throws. A host designed to boot with every
        // optional value blank must not fail to boot because one of them is blank.
        var baseAddress = configuration["Hindsight:BaseAddress"];
        var options = new HindsightOptions
        {
            ApiKey = apiKey,
            BaseAddress = string.IsNullOrWhiteSpace(baseAddress)
                ? HindsightOptions.DefaultBaseAddress
                : new Uri(baseAddress),
        };

        // Same explicit-timeout / no-retry discipline as the other outbound clients, and for the same
        // reason: AgentGrain is non-reentrant, so four attempts at a stalled memory call is four times a
        // stalled turn. The client already sends retain with async:true so the server does not run LLM
        // fact extraction inline, but the round trip still sits on the user's turn.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(nameof(HindsightClient), http => http.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddSingleton(sp => new HindsightIntegration(
            new HindsightClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HindsightClient)),
                options,
                sp.GetRequiredService<ILogger<HindsightClient>>()),
            configuration["Hindsight:Mission"]));
    }
}
