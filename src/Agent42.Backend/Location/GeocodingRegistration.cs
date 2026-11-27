using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Registers reverse-geocoding for the <c>get_current_location</c> tool (ADR 0012).
///
/// <para>Lifted out of <c>Program.cs</c> (cc-06), the <see cref="HindsightRegistration"/> shape.</para>
/// </summary>
public static class GeocodingRegistration
{
    /// <summary>
    /// Registers the Google geocoder when <c>Geocoding:GoogleApiKey</c> is set, else the coordinates-only one.
    /// A missing key is NOT eager-validated into a startup throw — it degrades: the tool still reports the user's
    /// coordinates, it just cannot name the street.
    /// </summary>
    public static void AddGeocoding(this IServiceCollection services, IConfiguration configuration)
    {
        var googleApiKey = configuration["Geocoding:GoogleApiKey"];
        if (string.IsNullOrWhiteSpace(googleApiKey))
        {
            services.AddSingleton<IGeocoder, CoordinatesOnlyGeocoder>();
            return;
        }

        var baseAddress = configuration["Geocoding:BaseAddress"] ?? GoogleGeocoder.DefaultBaseAddress;

        // Same explicit-timeout / no-retry discipline as the web clients, and for the same reason:
        // AgentGrain is non-reentrant, so a retried geocode multiplies the stall on the user's turn. 5s,
        // because the user's turn is blocked on it — and a geocode that hasn't answered by then is not
        // worth the wait (the tool degrades to coordinates).
#pragma warning disable EXTEXP0001
        services.AddHttpClient(GoogleGeocoder.HttpClientName, http =>
        {
            http.BaseAddress = new Uri(baseAddress);
            http.Timeout = TimeSpan.FromSeconds(5);
        })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddSingleton<IGeocoder>(sp => new GoogleGeocoder(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleGeocoder.HttpClientName),
            googleApiKey,
            sp.GetRequiredService<ILogger<GoogleGeocoder>>()));
    }
}
