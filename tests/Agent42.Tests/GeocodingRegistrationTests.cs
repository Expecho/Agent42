using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The startup decision geocoding owns (ADR 0012): a missing Google key degrades to coordinates, never throws.
/// </summary>
public sealed class GeocodingRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] entries)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeocoding(config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoKey_FallsBackToTheCoordinatesOnlyGeocoder(string? key)
    {
        using var sp = Build(("Geocoding:GoogleApiKey", key));

        Assert.IsType<CoordinatesOnlyGeocoder>(sp.GetRequiredService<IGeocoder>());
    }

    [Fact]
    public void Key_RegistersTheGoogleGeocoder_OnAShortTimeoutClient()
    {
        using var sp = Build(("Geocoding:GoogleApiKey", "KEY"), ("Geocoding:BaseAddress", "http://geo.test"));

        Assert.IsType<GoogleGeocoder>(sp.GetRequiredService<IGeocoder>());
        using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleGeocoder.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(5), http.Timeout);
        Assert.Equal(new Uri("http://geo.test"), http.BaseAddress);
    }

    [Fact]
    public void Key_WithoutABaseAddress_UsesTheDefault()
    {
        using var sp = Build(("Geocoding:GoogleApiKey", "KEY"));

        using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleGeocoder.HttpClientName);
        Assert.Equal(new Uri(GoogleGeocoder.DefaultBaseAddress), http.BaseAddress);
    }
}
