using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IGeocoder"/> over the Google Geocoding API (ADR 0012). Reverse-geocodes the browser's
/// fix into a street address so <c>get_current_location</c> can tell the model where the user is.
///
/// <para><b>This is not an SSRF sink, and the reason is narrow — do not add an address guard here on
/// the theory that any outbound call needs one.</b> Two properties make the claim true: the host is
/// <b>fixed</b> (config-pinned, never model-supplied), and the tool that reaches this class takes
/// <b>zero model-supplied parameters</b> — the only variable input is a lat/lon pair that came from the
/// <i>browser</i> and was range-validated server-side, and a <c>double</c> cannot inject a host. Both
/// properties are load-bearing: adding, say, a <c>string address</c> parameter to the tool would turn
/// this into an SSRF sink while this comment still sat here reassuring the reader.
/// <c>GetCurrentLocation_ExposesNoParameters</c> pins the second property with a test rather than
/// trusting the comment.</para>
///
/// <para><b>Nothing here logs the coordinates or the key.</b> Do not copy
/// <see cref="SearxngWebSearch"/>'s query logging — its query is a search term, this one is where a
/// named human is standing. The key rides in the query string, which OpenTelemetry's HttpClient
/// instrumentation redacts to <c>?*</c> by default (verified on
/// OpenTelemetry.Instrumentation.Http 1.15.1), so it does not reach the collector on the outbound
/// span either — but that is a library default, and <c>user.*</c> identity is stamped onto every span
/// (ADR 0010), so the span would otherwise join this named human to their exact position.</para>
///
/// The <see cref="HttpClient"/> is a named client with an explicit timeout and <b>no</b> resilience
/// pipeline (see Program.cs): <c>AgentGrain</c> is non-reentrant, so a retried geocode multiplies the
/// stall on the user's turn.
/// </summary>
public sealed class GoogleGeocoder(HttpClient httpClient, string apiKey, ILogger<GoogleGeocoder> logger)
    : IGeocoder
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> this service resolves.</summary>
    public const string HttpClientName = "google-geocoding";

    /// <summary>The API's base address. Config-pinned — half of the "not an SSRF sink" argument.</summary>
    public const string DefaultBaseAddress = "https://maps.googleapis.com";

    public async Task<GeocodedPlace?> ReverseAsync(
        UserLocation location, CancellationToken cancellationToken = default)
    {
        if (!location.IsValid)
        {
            return null;
        }

        try
        {
            // InvariantCulture is load-bearing, not a style choice. A double interpolated under the
            // ambient culture renders 52,3676 on a nl-NL machine, so latlng=52,3676,4,9041 — which
            // Google rejects. Nothing in this repo sets InvariantGlobalization, so the ambient-culture
            // bug would break on the developer's machine and work in the Linux container: the worst
            // way round. Reverse_SendsInvariantCultureCoordinates asserts on the request URI itself.
            var latitude = location.Latitude.ToString(CultureInfo.InvariantCulture);
            var longitude = location.Longitude.ToString(CultureInfo.InvariantCulture);

            var requestUri = "maps/api/geocode/json"
                + $"?latlng={Uri.EscapeDataString($"{latitude},{longitude}")}"
                + $"&key={Uri.EscapeDataString(apiKey)}";

            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Degrade, never throw into the middle of the user's turn: the tool still reports the
                // coordinates, it just cannot name the street. The URI is NOT logged — it carries the key.
                logger.LogWarning(
                    "Reverse geocoding failed with HTTP {StatusCode}; returning coordinates without an address.",
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return Parse(json.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reason: a geocode failure must cost the user an address, not their whole reply.
            // ex.Message can carry the request URI (and therefore the key) on some HttpRequestException
            // paths, so log the exception type only — never the message or the URI.
            logger.LogWarning(
                "Reverse geocoding threw {ExceptionType}; returning coordinates without an address.",
                ex.GetType().Name);
            return null;
        }
    }

    private GeocodedPlace? Parse(JsonElement root)
    {
        var status = GetString(root, "status");

        // ZERO_RESULTS is an ordinary answer (a fix in the middle of the North Sea has no address),
        // not a failure. Everything else non-OK is a real problem worth a warning — but still degrades
        // to null rather than throwing. The status string is safe to log; it names no coordinates.
        if (status != "OK")
        {
            if (status != "ZERO_RESULTS")
            {
                logger.LogWarning(
                    "Reverse geocoding returned status {Status}; returning coordinates without an address.",
                    status);
            }

            return null;
        }

        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return null;
        }

        // Google returns the most specific match first, then progressively coarser ones (street →
        // neighbourhood → city → country). The first is the one worth having.
        var best = results[0];

        var formatted = GetString(best, "formatted_address");
        if (string.IsNullOrWhiteSpace(formatted))
        {
            return null;
        }

        // Everything else in the response — every candidate result, every address component, the
        // bounding geometry — is dropped on the floor. This return value is persisted into the 2 MB
        // session item and re-sent to the model every later turn (ADR 0004, GeoToolLimits).
        return new GeocodedPlace(
            Truncate(formatted, GeoToolLimits.MaxFormattedAddressLength),
            Truncate(FindComponent(best, "locality"), GeoToolLimits.MaxPlaceNameLength),
            Truncate(FindComponent(best, "country"), GeoToolLimits.MaxPlaceNameLength));
    }

    // Pulls one named address component (e.g. "locality", "country") out of a result's components array.
    private static string? FindComponent(JsonElement result, string type)
    {
        if (!result.TryGetProperty("address_components", out var components)
            || components.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var component in components.EnumerateArray())
        {
            if (!component.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var candidate in types.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String
                    && candidate.GetString() == type)
                {
                    return GetString(component, "long_name");
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
