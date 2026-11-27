using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The <c>get_current_location</c> tool (ADR 0012, made a client tool by ADR 0093). The browser answers it
/// through the ADR 0089 round trip only when the model calls it, so the position no longer rides every chat
/// request. This tool is deliberately NOT an SSRF sink and must not grow an address guard; see the ADR 0012
/// gotchas.
///
/// <para>Built per grain activation with <c>new</c> (public only because this repo has no <c>InternalsVisibleTo</c>) (ADR 0106's pattern, extended by the leaf-tool ADR). It reaches
/// the browser only through <paramref name="awaitClientResult"/>, the grain's client-tool wait, so it holds no
/// grain state.</para>
/// </summary>
public sealed class LocationTools(
    IGeocoder geocoder,
    ILogger logger,
    string grainKey,
    Func<string, CancellationToken, Task<string>> awaitClientResult)
{
    private const string NotSharedText =
        "The user has not shared their location with this conversation, so I don't know " +
        "where they are. Do not guess or assume a location. Tell the user you need their " +
        "location and ask them to enable location sharing in Settings, then try again.";

    private const string NoFixText =
        "The user's device could not get a position right now, so I don't know where they are. " +
        "Do not guess or assume a location. Tell the user their location could not be determined " +
        "and ask them to try again in a moment.";

    private const string UnavailableText =
        "No browser is connected to this chat right now (for example, this is a scheduled or " +
        "background run), so the user's location cannot be read. Do not guess or assume a location.";

    /// <summary>
    /// The <c>get_current_location</c> function as the model sees it. Zero parameters, and that is load-bearing (ADR 0012).
    /// The model cannot supply coordinates — the position comes from the browser, as the answer to this client tool
    /// (ADR 0093), parsed and range-checked by the tool — which is half of why this tool is not an SSRF sink despite
    /// making an outbound call (the other half: the geocoder's host is config-pinned).
    /// <c>GetCurrentLocation_ExposesNoParameters</c> pins the shape, because a later contributor adding a
    /// <c>string address</c> parameter would quietly turn it into one.
    /// </summary>
    public AIFunction Function() => AIFunctionFactory.Create(
        GetCurrentLocationAsync,
        name: ClientToolLimits.GetCurrentLocation,
        description: "Get the user's current location from their device: their coordinates and, when " +
            "it can be resolved, their street address. Use this for anything that depends on where the " +
            "user is — local weather, what's nearby, travel times, 'where am I'. If the user has not " +
            "shared their location, or the device cannot get a position, this says so; ask them to " +
            "share it rather than guessing.");

    /// <summary>
    /// Agent tool: reports where the user is (ADR 0012). Exposed to the model as
    /// <c>get_current_location</c>.
    ///
    /// <para><b>It takes no parameters, and must never grow one.</b> The position comes from the
    /// browser, as its answer to this client tool (ADR 0093) — the model does not get to say where the
    /// user is, or to name a place to look up. The answer is parsed as numbers and range-checked with
    /// <see cref="UserLocation.IsValid"/> before the geocoder sees it; anything else is "no usable fix".
    /// That, with the geocoder's config-pinned host, is what keeps the outbound geocode call from being an
    /// SSRF sink, and it is pinned by <c>GetCurrentLocation_ExposesNoParameters</c> rather than by this
    /// comment.</para>
    ///
    /// <para>When there is no position — the user never granted permission, the device could not get a
    /// fix, or no browser can answer — the model is told so in plain language and told not to guess. The
    /// raw status JSON is never the result: the model needs the instruction, not the code.</para>
    ///
    /// <para>The <see cref="CancellationToken"/> is <b>not</b> an exception to the zero-parameter rule:
    /// <c>AIFunctionFactory</c> supplies it from the invocation context and keeps it out of the JSON
    /// schema, so the model never sees it and cannot set it. It is here because the grain is
    /// non-reentrant — an abandoned turn should not hold the activation for the wait or the geocode's full
    /// timeout. The security property is about the <i>schema</i>, which is what <c>GetCurrentLocation_ExposesNoParameters</c> asserts on.</para>
    /// </summary>
    internal async Task<string> GetCurrentLocationAsync(CancellationToken cancellationToken)
    {
        var answer = await awaitClientResult(ClientToolLimits.GetCurrentLocation, cancellationToken);
        var (location, status) = ParseBrowserFix(answer);
        if (location is null)
        {
            logger.LogInformation(
                "Agent grain {GrainKey} has no usable location from the browser ({Status})",
                grainKey, status);
            return status switch
            {
                "not_shared" => NotSharedText,
                "unavailable" => UnavailableText,
                _ => NoFixText,
            };
        }

        var place = await geocoder.ReverseAsync(location, cancellationToken);

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
        logger.LogInformation(
            "Agent grain {GrainKey} reported the user's current location (address resolved: {Resolved})",
            grainKey, place is not null);

        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Reads the browser's answer: a valid fix, or null with the reason. The reason is the browser's own
    /// <c>status</c> (<c>not_shared</c>, <c>no_fix</c>) or the broker's (<c>unavailable</c>, <c>timeout</c>);
    /// anything else — not JSON (a truncated answer is a cut prefix), not numbers, out of range — is
    /// <c>invalid</c> and never reaches the geocoder.
    /// </summary>
    private static (UserLocation? Fix, string Status) ParseBrowserFix(string answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "invalid");
            }

            if (root.TryGetProperty("status", out var status))
            {
                return (null, status.ValueKind == JsonValueKind.String ? status.GetString() ?? "invalid" : "invalid");
            }

            var fix = ReadFix(root);
            return fix is { IsValid: true } ? (fix, "ok") : (null, "invalid");
        }
        catch (JsonException)
        {
            return (null, "invalid");
        }
    }

    private static UserLocation? ReadFix(JsonElement root)
        => ReadDouble(root, "latitude") is { } latitude && ReadDouble(root, "longitude") is { } longitude
            ? new UserLocation(latitude, longitude, ReadDouble(root, "accuracyMetres"), ReadInstant(root, "capturedAt"))
            : null;

    private static double? ReadDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            ? number
            : null;

    private static DateTimeOffset? ReadInstant(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            ? instant
            : null;
}
