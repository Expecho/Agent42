
namespace Agent42.Agents;

/// <summary>
/// The budget the <c>get_current_location</c> tool's <b>return value</b> must fit in — the same
/// discipline, and for the same reason, as <see cref="WebToolLimits"/>: a tool's
/// <c>FunctionResultContent</c> is <b>persisted</b> into the session's single <c>agent-state</c>
/// Cosmos item, which is hard-capped at 2 MB (ADR 0004), and re-sent to the model every later turn.
///
/// A raw Google Geocoding response is a large JSON document — several candidate results, each with
/// every address component and its bounding geometry. None of it is returned. The tool hands back a
/// compact record, and the one free-form field in it (the formatted address) is capped here.
/// </summary>
public static class GeoToolLimits
{
    /// <summary>
    /// Maximum length of a reverse-geocoded formatted address, in characters. Matches
    /// <see cref="WebToolLimits.MaxSnippetLength"/> — a street address that needs more than this is
    /// not an address any more.
    /// </summary>
    public const int MaxFormattedAddressLength = WebToolLimits.MaxSnippetLength;

    /// <summary>Maximum length of a locality or country name, in characters.</summary>
    public const int MaxPlaceNameLength = 100;
}
