namespace Agent42.Agents;

/// <summary>
/// The user's position for a single turn, captured by the browser's <c>navigator.geolocation</c>
/// and carried in on the chat request (ADR 0012).
///
/// <para><b>Why it rides the request.</b> Geolocation is a <i>browser</i> API and tools run
/// server-side inside <c>AgentGrain</c>; the chat path is a one-way NDJSON stream from grain to
/// client, so a tool cannot ask the browser for a fix mid-turn. The position must therefore travel
/// with the turn that needs it — the same shape <see cref="AttachmentRef"/> uses.</para>
///
/// <para><b>It is per-turn, and deliberately not persisted.</b> It never lands in
/// <c>AgentGrainState</c>: a fix kept across turns would have the model confidently reporting where
/// the user was last week. It lives in a turn-scoped field that <c>AgentGrain</c> clears explicitly.</para>
///
/// Crosses the grain boundary (passed into <c>ChatAsync</c>), so it is Orleans-serializable with
/// explicit member ids — mirror <see cref="AttachmentRef"/>, not the plain-interface shape.
/// </summary>
/// <param name="Latitude">Degrees north, -90 to 90. Validated at the API boundary.</param>
/// <param name="Longitude">Degrees east, -180 to 180. Validated at the API boundary.</param>
/// <param name="AccuracyMetres">The browser's own confidence radius, if it reported one.</param>
/// <param name="CapturedAt">When the browser took the fix, so the model can say how old it is.</param>
[GenerateSerializer]
[Alias("Agent42.Agents.UserLocation")]
public sealed record UserLocation(
    [property: Id(0)] double Latitude,
    [property: Id(1)] double Longitude,
    [property: Id(2)] double? AccuracyMetres = null,
    [property: Id(3)] DateTimeOffset? CapturedAt = null)
{
    /// <summary>
    /// True when the coordinates are within the valid ranges. A fix that fails this is dropped at
    /// the API boundary rather than failing the user's turn — a bad position is not worth a 400.
    /// </summary>
    public bool IsValid =>
        double.IsFinite(Latitude) && double.IsFinite(Longitude)
        && Latitude is >= -90 and <= 90
        && Longitude is >= -180 and <= 180;
}
