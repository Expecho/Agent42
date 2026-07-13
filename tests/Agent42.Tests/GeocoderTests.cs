using Agent42.Agents;

namespace Agent42.Tests;

public sealed class GeocoderTests
{
    // AC10 — with no API key configured the app still runs and the tool still works: the offline
    // geocoder resolves no address, so get_current_location reports coordinates without a street.
    // A missing key is a degradation, not an outage (unlike the embedding deployment — ADR 0007).
    [Fact]
    public async Task CoordinatesOnlyGeocoder_ReturnsNoAddress()
    {
        var place = await new CoordinatesOnlyGeocoder()
            .ReverseAsync(new UserLocation(52.3676, 4.9041));

        Assert.Null(place);
    }

    // The range check that lets the API boundary drop a bad fix instead of failing the user's turn.
    [Theory]
    [InlineData(52.3676, 4.9041, true)]
    [InlineData(-90, -180, true)]
    [InlineData(90, 180, true)]
    [InlineData(90.1, 0, false)]
    [InlineData(-90.1, 0, false)]
    [InlineData(0, 180.1, false)]
    [InlineData(0, -180.1, false)]
    [InlineData(double.NaN, 0, false)]
    [InlineData(0, double.PositiveInfinity, false)]
    public void UserLocation_IsValid_RangeChecksCoordinates(double lat, double lon, bool expected) =>
        Assert.Equal(expected, new UserLocation(lat, lon).IsValid);
}
