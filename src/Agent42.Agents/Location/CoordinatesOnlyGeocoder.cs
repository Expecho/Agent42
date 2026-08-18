namespace Agent42.Agents;

/// <summary>
/// The offline <see cref="IGeocoder"/>: resolves no address at all. Used by DevHost, the AppHost and
/// the tests, and by the Backend itself when no <c>Geocoding:GoogleApiKey</c> is configured.
///
/// This is what makes a missing API key a <b>degradation rather than an outage</b>: the
/// <c>get_current_location</c> tool still works and still reports the user's coordinates and how old
/// the fix is — it just cannot name the street. Unlike the embedding deployment (ADR 0007), an absent
/// key is not eager-validated into a startup throw.
/// </summary>
public sealed class CoordinatesOnlyGeocoder : IGeocoder
{
    public Task<GeocodedPlace?> ReverseAsync(
        UserLocation location, CancellationToken cancellationToken = default)
        => Task.FromResult<GeocodedPlace?>(null);
}
