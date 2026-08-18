using System.Globalization;
using Agent42.Backend;
using Microsoft.Extensions.Logging;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// These tests dial a real loopback socket (<see cref="TestHttpServer"/>) rather than stubbing the
/// transport, because two of the things under test are properties of the <b>request that actually goes
/// out</b>: that the coordinates are formatted invariantly (AC6) and that the API key never escapes into
/// a log (AC8). A stub handler can show the first; only a real request head shows what was truly sent.
/// </summary>
public sealed class GoogleGeocoderTests
{
    private const string ApiKey = "test-key-SHOULD-NEVER-BE-LOGGED";

    private const string AmsterdamJson = """
        {
          "status": "OK",
          "results": [
            {
              "formatted_address": "Damrak 1, 1012 LG Amsterdam, Netherlands",
              "address_components": [
                { "long_name": "1",           "short_name": "1",   "types": ["street_number"] },
                { "long_name": "Damrak",      "short_name": "Damrak", "types": ["route"] },
                { "long_name": "Amsterdam",   "short_name": "Amsterdam", "types": ["locality", "political"] },
                { "long_name": "Netherlands", "short_name": "NL",  "types": ["country", "political"] }
              ]
            },
            {
              "formatted_address": "Amsterdam, Netherlands",
              "address_components": []
            }
          ]
        }
        """;

    private static readonly UserLocation Dam = new(52.3676, 4.9041, AccuracyMetres: 12);

    // AC6 — a real Google payload maps onto a GeocodedPlace: the FIRST (most specific) result's
    // formatted address, plus the locality and country address components.
    [Fact]
    public async Task Reverse_ParsesFormattedAddress()
    {
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(200, "application/json", AmsterdamJson));

        var place = await Build(server, out _).ReverseAsync(Dam);

        Assert.NotNull(place);
        Assert.Equal("Damrak 1, 1012 LG Amsterdam, Netherlands", place.FormattedAddress);
        Assert.Equal("Amsterdam", place.Locality);
        Assert.Equal("Netherlands", place.Country);
    }

    // AC6 — the coordinates must be formatted with InvariantCulture. Under nl-NL an ambient-culture
    // double renders "52,3676", so the query becomes latlng=52,3676,4,9041 and Google rejects it. The
    // bug therefore breaks on a Dutch developer's machine and works in the Linux container — the worst
    // way round — so this asserts on the request line the server ACTUALLY received, not on the parsed
    // result (which a stub would happily produce either way).
    [Fact]
    public async Task Reverse_SendsInvariantCultureCoordinates()
    {
        string? startLine = null;
        await using var server = TestHttpServer.Start(line =>
        {
            startLine = line;
            return new TestHttpServer.HttpReply(200, "application/json", AmsterdamJson);
        });

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            await Build(server, out _).ReverseAsync(Dam);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.NotNull(startLine);
        // The pair is escaped, so the separating comma arrives as %2C — what matters is that the
        // decimal separator is a '.' and there are exactly two numbers.
        Assert.Contains("latlng=52.3676%2C4.9041", startLine);
        Assert.DoesNotContain("52,3676", startLine);
    }

    // AC7 — every upstream failure degrades to null. A geocode must never throw into the middle of the
    // user's turn: the tool still reports coordinates, it just cannot name the street. ZERO_RESULTS is
    // an ordinary answer (a fix in the North Sea has no address), the others are real problems.
    [Theory]
    [InlineData(200, """{ "status": "ZERO_RESULTS", "results": [] }""")]
    [InlineData(200, """{ "status": "REQUEST_DENIED", "error_message": "The provided API key is invalid." }""")]
    [InlineData(200, """{ "status": "OVER_QUERY_LIMIT" }""")]
    [InlineData(200, """{ "status": "OK", "results": [] }""")]
    [InlineData(500, "upstream exploded")]
    [InlineData(403, "")]
    public async Task Reverse_DegradesToNull_OnZeroResultsAndErrors(int status, string body)
    {
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(status, "application/json", body));

        Assert.Null(await Build(server, out _).ReverseAsync(Dam));
    }

    // AC8 — the key is a secret. It rides in the query string, so the easy mistake is to log the
    // request URI (or an HttpRequestException's message, which can embed it) when a call fails. It must
    // appear in NO log message and in NO returned value. Exercised on both the success and the failure
    // path, because the failure path is where a URI usually gets logged "to help debugging".
    [Theory]
    [InlineData(200, AmsterdamJson)]
    [InlineData(200, """{ "status": "REQUEST_DENIED" }""")]
    [InlineData(500, "boom")]
    public async Task Reverse_NeverLeaksTheApiKey(int status, string body)
    {
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(status, "application/json", body));

        var place = await Build(server, out var log).ReverseAsync(Dam);

        Assert.All(log.Messages, message => Assert.DoesNotContain(ApiKey, message));

        // And the coordinates themselves — which are what makes this PII — stay out of the logs too.
        Assert.All(log.Messages, message => Assert.DoesNotContain("52.3676", message));

        if (place is not null)
        {
            Assert.DoesNotContain(ApiKey, place.FormattedAddress);
        }
    }

    // The returned address is BUDGETED (GeoToolLimits), for the same reason the web tools' results are
    // (WebToolLimits): this value becomes a FunctionResultContent, which is persisted into the session's
    // single 2 MB Cosmos item and re-sent to the model every later turn (ADR 0004). A geocoder is a
    // third party — it does not get to decide how many bytes of ours it consumes forever. Feeds an
    // absurd address and asserts the cap binds, the same way SearxngWebSearchTests feeds an oversized
    // snippet; without this, deleting the truncation would go unnoticed.
    [Fact]
    public async Task Reverse_CapsTheFormattedAddressAndPlaceNames()
    {
        var huge = new string('x', GeoToolLimits.MaxFormattedAddressLength * 3);
        var hugePlace = new string('y', GeoToolLimits.MaxPlaceNameLength * 3);
        await using var server = TestHttpServer.Start(new TestHttpServer.HttpReply(200, "application/json", $$"""
            {
              "status": "OK",
              "results": [{
                "formatted_address": "{{huge}}",
                "address_components": [
                  { "long_name": "{{hugePlace}}", "types": ["locality"] },
                  { "long_name": "{{hugePlace}}", "types": ["country"] }
                ]
              }]
            }
            """));

        var place = await Build(server, out _).ReverseAsync(Dam);

        Assert.NotNull(place);
        Assert.Equal(GeoToolLimits.MaxFormattedAddressLength, place.FormattedAddress.Length);
        Assert.Equal(GeoToolLimits.MaxPlaceNameLength, place.Locality!.Length);
        Assert.Equal(GeoToolLimits.MaxPlaceNameLength, place.Country!.Length);
    }

    // A malformed body is just another degradation, not an exception into the turn.
    [Fact]
    public async Task Reverse_OnUnparseableBody_ReturnsNull()
    {
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(200, "application/json", "this is not json"));

        Assert.Null(await Build(server, out _).ReverseAsync(Dam));
    }

    // An out-of-range fix never reaches the wire (the API boundary drops these too — AC9 — but the
    // geocoder does not trust its caller).
    [Theory]
    [InlineData(91, 0)]
    [InlineData(0, 181)]
    [InlineData(double.NaN, 0)]
    public async Task Reverse_InvalidCoordinates_ReturnsNullWithoutCalling(double lat, double lon)
    {
        var called = false;
        await using var server = TestHttpServer.Start(_ =>
        {
            called = true;
            return new TestHttpServer.HttpReply(200, "application/json", AmsterdamJson);
        });

        Assert.Null(await Build(server, out _).ReverseAsync(new UserLocation(lat, lon)));
        Assert.False(called);
    }

    private static GoogleGeocoder Build(TestHttpServer server, out RecordingLogger log)
    {
        log = new RecordingLogger();
        return new GoogleGeocoder(
            new HttpClient { BaseAddress = new Uri($"{server.BaseUrl}/") },
            ApiKey,
            log);
    }

    /// <summary>Captures every formatted log message so a test can assert a secret never reached one.</summary>
    private sealed class RecordingLogger : ILogger<GoogleGeocoder>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }
}
