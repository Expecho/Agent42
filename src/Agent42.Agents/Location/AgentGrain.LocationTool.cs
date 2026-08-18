using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The <c>get_current_location</c> tool (ADR 0012). It reads the turn-scoped <c>_turnLocation</c>
/// field declared in the core part — the browser fix rides the chat request, because a tool cannot
/// ask the browser for a position mid-turn. This tool is deliberately NOT an SSRF sink and must not
/// grow an address guard; see the ADR 0012 gotchas.
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// Agent tool: reports where the user is (ADR 0012). Exposed to the model as
    /// <c>get_current_location</c>.
    ///
    /// <para><b>It takes no parameters, and must never grow one.</b> The position comes from the
    /// browser on this turn's request — the model does not get to say where the user is, or to name a
    /// place to look up. That is what keeps the outbound geocode call from being an SSRF sink (with the
    /// geocoder's config-pinned host), and it is pinned by <c>GetCurrentLocation_ExposesNoParameters</c>
    /// rather than by this comment.</para>
    ///
    /// <para>When the browser sent no position — the user never granted permission, or the fix went
    /// stale — the model is told so in plain language and asked to prompt the user. The tool is the only
    /// thing that knows; nothing else in the turn can surface it.</para>
    ///
    /// <para>The <see cref="CancellationToken"/> is <b>not</b> an exception to the zero-parameter rule:
    /// <c>AIFunctionFactory</c> supplies it from the invocation context and keeps it out of the JSON
    /// schema, so the model never sees it and cannot set it. It is here because the grain is
    /// non-reentrant — an abandoned turn should not hold the activation for the geocode's full timeout.
    /// The security property is about the <i>schema</i>, which is what AC13 asserts on.</para>
    /// </summary>
    private async Task<string> GetCurrentLocationAsync(CancellationToken cancellationToken)
    {
        if (_turnLocation is not { } location)
        {
            return "The user has not shared their location with this conversation, so I don't know " +
                "where they are. Do not guess or assume a location. Tell the user you need their " +
                "location and ask them to enable location sharing in Preferences, then try again.";
        }

        var place = await _geocoder.ReverseAsync(location, cancellationToken);

        // Only the fields worth persisting. A raw Google response carries several candidate results,
        // every address component and the bounding geometry — none of that is returned, because this
        // value goes into the session's 2 MB Cosmos item and is re-sent to the model every later turn
        // (ADR 0004; the address is budgeted by GeoToolLimits).
        var result = new
        {
            latitude = location.Latitude,
            longitude = location.Longitude,
            accuracyMetres = location.AccuracyMetres,
            address = place?.FormattedAddress,
            locality = place?.Locality,
            country = place?.Country,
            // WHEN the fix was taken, as an absolute instant — deliberately NOT a relative "age in
            // minutes". This value is persisted into chat history and re-sent to the model on every
            // later turn, so a relative age decays into a lie: a fix that was 6 seconds old when the
            // tool ran would still claim "fixAgeMinutes: 0.1" three hours and ten turns later, which is
            // exactly the stale-position confidence the field existed to prevent. An absolute timestamp
            // stays true forever; the model has get_date to work out the age against it.
            capturedAt = location.CapturedAt?.ToString("o", CultureInfo.InvariantCulture),
            note = place is null
                ? "Coordinates only — no street address could be resolved for this position."
                : null,
        };

        // Never log the coordinates: they are where a named human is standing, and this repo stamps
        // user identity onto every span (ADR 0010). Log that the tool ran, not what it found.
        _logger.LogInformation(
            "Agent grain {GrainKey} reported the user's current location (address resolved: {Resolved})",
            this.GetPrimaryKeyString(), place is not null);

        return JsonSerializer.Serialize(result);
    }
}
