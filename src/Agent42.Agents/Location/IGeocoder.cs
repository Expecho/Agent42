namespace Agent42.Agents;

/// <summary>
/// Turns a <see cref="UserLocation"/> into a human-readable place (ADR 0012). Used by the
/// <c>get_current_location</c> tool so the model can say "you're on Damrak in Amsterdam" rather
/// than reciting coordinates at the user.
///
/// Production reverse-geocodes through the Google Geocoding API (<c>GoogleGeocoder</c>); local dev,
/// the AppHost and tests use <see cref="CoordinatesOnlyGeocoder"/> — the same real/in-memory split as
/// <see cref="IWebSearch"/>, <see cref="IAttachmentStore"/> and <see cref="IDocumentIndex"/>. Without
/// an API key the feature degrades to coordinates rather than vanishing.
///
/// <para><b>The coordinates leave our infrastructure.</b> A reverse geocode sends the user's exact
/// position to Google. That is an accepted cost of the feature, recorded in ADR 0012 — not something
/// the interface hides.</para>
/// </summary>
public interface IGeocoder
{
    /// <summary>
    /// Returns the place at <paramref name="location"/>, or <c>null</c> when no address could be
    /// resolved. <b>Null is not an error</b> — it is also what a mid-ocean fix, an unconfigured
    /// geocoder, or a failed upstream call returns. Implementations log and degrade; they never
    /// throw into the middle of the user's turn.
    ///
    /// Implementations truncate to <see cref="GeoToolLimits"/> — the result is persisted into the
    /// session (ADR 0004), so the caller does not get to receive an unbounded payload.
    /// </summary>
    Task<GeocodedPlace?> ReverseAsync(UserLocation location, CancellationToken cancellationToken = default);
}

/// <summary>
/// A reverse-geocoded place: the few fields worth persisting, not the geocoder's whole response.
/// </summary>
/// <param name="FormattedAddress">The full address as one line, capped at <see cref="GeoToolLimits.MaxFormattedAddressLength"/>.</param>
/// <param name="Locality">City or town, when the geocoder identified one.</param>
/// <param name="Country">Country, when the geocoder identified one.</param>
public sealed record GeocodedPlace(string FormattedAddress, string? Locality, string? Country);
