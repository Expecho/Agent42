using Agent42.Agents;
using Agent42.Backend;
using Hindsight.AgentFramework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The one startup decision long-term memory owns (ADR 0056): whether memory is on at all.
///
/// <para>Worth testing directly rather than through a host, because the OFF branch is the default and the
/// distinction it encodes is easy to erode. <c>HindsightIntegration(null)</c> does not mean "a client that
/// will fail"; it means <c>BuildAgent</c> attaches no provider, so a turn makes <b>zero</b> Hindsight
/// calls. Someone later replacing the null with a <c>NullHindsightClient</c> "for tidiness" would keep
/// every test here green while turning the attach-nothing branch into an attach-and-no-op one.</para>
/// </summary>
public sealed class HindsightRegistrationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static HindsightIntegration Resolve(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHindsightMemory(config);
        return services.BuildServiceProvider().GetRequiredService<HindsightIntegration>();
    }

    // The default. No key configured anywhere → memory off, and off means UNATTACHED.
    [Fact]
    public void NoApiKey_RegistersAnIntegrationWithNoClient()
    {
        var integration = Resolve(Config());

        Assert.Null(integration.Client);
        Assert.Null(integration.Mission);
    }

    // Blank and whitespace are the shapes an empty GitHub secret actually arrives as — an unset
    // `HINDSIGHT_API_KEY` threads through Bicep as "" — so they must read as absent, not as a key.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankApiKey_IsTreatedAsAbsent(string apiKey)
        => Assert.Null(Resolve(Config(("Hindsight:ApiKey", apiKey))).Client);

    [Fact]
    public void ApiKey_RegistersARealClient()
    {
        var integration = Resolve(Config(("Hindsight:ApiKey", "SECRET-hindsight-key")));

        Assert.NotNull(integration.Client);
        Assert.IsType<HindsightClient>(integration.Client);
    }

    // Mission has to survive the trip or Hindsight:Mission is read from config and dropped on the floor —
    // silently, since a null mission simply means "never create the bank".
    [Fact]
    public void Mission_IsCarriedOntoTheIntegration()
    {
        var integration = Resolve(Config(
            ("Hindsight:ApiKey", "SECRET-hindsight-key"),
            ("Hindsight:Mission", "Remember this user's preferences.")));

        Assert.Equal("Remember this user's preferences.", integration.Mission);
    }

    // A mission with no key is still off: the key is the switch, not the mission.
    [Fact]
    public void MissionWithoutAKey_IsStillOff()
    {
        var integration = Resolve(Config(("Hindsight:Mission", "Remember everything.")));

        Assert.Null(integration.Client);
    }

    // The named client exists only on the ON branch. That asymmetry is deliberate and load-bearing: a
    // client registered when no key is present would be created by CreateClient with the resilience
    // pipeline AddServiceDefaults attaches, and AgentGrain is non-reentrant, so four attempts at a stalled
    // memory call is four times a stalled turn (the named-HttpClient gotcha).
    [Fact]
    public void NamedHttpClient_IsRegisteredOnlyWhenAKeyIsPresent()
    {
        Assert.Null(WithMemory(Config()).GetService<IHttpClientFactory>());
        Assert.NotNull(WithMemory(Config(("Hindsight:ApiKey", "k"))).GetService<IHttpClientFactory>());

        static ServiceProvider WithMemory(IConfiguration config)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHindsightMemory(config);
            return services.BuildServiceProvider();
        }
    }

    // A self-hosted server is the reason BaseAddress is configurable at all; the default is the cloud.
    // Asserted through a real request rather than by reading the option back, because the client composes
    // the URL itself and that is the part that can break.
    [Fact]
    public async Task BaseAddress_OverridesTheCloudDefault()
    {
        var handler = new RecordingHandler();
        var options = new HindsightOptions { ApiKey = "k", BaseAddress = new Uri("http://localhost:8888") };
        var client = new HindsightClient(new HttpClient(handler), options);

        await client.RecallAsync("user-1", "anything");

        Assert.StartsWith("http://localhost:8888/v1/default/banks/user-1/", handler.LastUrl);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[]}""", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
