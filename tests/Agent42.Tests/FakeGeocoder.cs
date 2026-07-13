using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// A deterministic <see cref="IGeocoder"/> for the grain tests: every valid fix resolves to one canned
/// address. <see cref="CoordinatesOnlyGeocoder"/> (the real offline implementation) resolves nothing at
/// all, so a test built on it could not tell "the address reached the model" apart from "there was no
/// address" — this fake is what lets AC1 assert the reverse-geocoded address actually came back.
///
/// Every silo configurator that hosts an <see cref="AgentGrain"/> must register an
/// <see cref="IGeocoder"/>, or that host's grain activations fail.
/// </summary>
public sealed class FakeGeocoder : IGeocoder
{
    /// <summary>The address every valid fix resolves to. Distinctive, so a test can assert on it.</summary>
    public const string Address = "Damrak 1, 1012 LG Amsterdam, Netherlands";

    public Task<GeocodedPlace?> ReverseAsync(
        UserLocation location, CancellationToken cancellationToken = default)
        => Task.FromResult<GeocodedPlace?>(
            location.IsValid ? new GeocodedPlace(Address, "Amsterdam", "Netherlands") : null);
}

/// <summary>
/// A silo like <see cref="TestSiloConfigurator"/>, but registering the <b>real</b>
/// <see cref="CoordinatesOnlyGeocoder"/> — the implementation the Backend falls back to when no
/// <c>Geocoding:GoogleApiKey</c> is configured, and the one DevHost and the AppHost always use.
///
/// It exists so the "a missing key is a degradation, not an outage" claim (ADR 0012) has an actual
/// behavioural test behind it. Every other grain test uses <see cref="FakeGeocoder"/>, which always
/// resolves an address — so on those silos the coordinates-without-an-address path never executes, and
/// a regression that broke it (or that started throwing when no address came back) would go unnoticed.
/// </summary>
public sealed class CoordinatesOnlyGeocoderSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient, FakeChatClient>();
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(sp.GetRequiredService<IChatClient>(), "fake-model"));
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            services.AddDocumentRagFakes();
            services.AddWebToolFakes();

            // Replaces the IGeocoder that AddWebToolFakes just registered (last registration wins for
            // a single resolve) — the point of this silo.
            services.AddSingleton<IGeocoder, CoordinatesOnlyGeocoder>();
        });
    }
}
