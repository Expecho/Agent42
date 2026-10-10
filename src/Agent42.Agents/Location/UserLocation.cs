namespace Agent42.Agents;

/// <summary>
/// The user's position, captured by the browser's <c>navigator.geolocation</c> (ADR 0012) and the
/// geocoder's input.
///
/// <para><b>The browser answers the tool.</b> Since ADR 0093 the position no longer rides the chat
/// request: <c>get_current_location</c> is a client tool (ADR 0089), the browser posts its fix as the
/// tool's result, and the grain parses it into this record only for the call that asked.</para>
///
/// <para><b>It is deliberately not persisted.</b> It never lands in <c>AgentGrainState</c>: a fix
/// kept across turns would have the model confidently reporting where the user was last week. It
/// lives only in the local variables of one tool call.</para>
///
/// It no longer crosses a grain boundary, but keeps its Orleans attributes: removing them is churn
/// with no benefit.
/// </summary>
/// <param name="Latitude">Degrees north, -90 to 90. Checked by <see cref="IsValid"/>.</param>
/// <param name="Longitude">Degrees east, -180 to 180. Checked by <see cref="IsValid"/>.</param>
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
    /// True when the coordinates are within the valid ranges. A fix that fails this is treated as
    /// "no usable fix" by the tool and never reaches the geocoder — a bad position is not worth failing
    /// the turn.
    /// </summary>
    public bool IsValid =>
        double.IsFinite(Latitude) && double.IsFinite(Longitude)
        && Latitude is >= -90 and <= 90
        && Longitude is >= -180 and <= 180;
}
