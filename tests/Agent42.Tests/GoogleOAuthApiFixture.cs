using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> for <see cref="GoogleOAuthEndpointTests"/>: client credentials in
/// configuration, a <see cref="GoogleOAuthClient"/> pointed at a fake authorization server, and a
/// connection-aware calendar double. The protector is exposed because the tests decrypt what the callback
/// persisted (ADR 0053).
/// </summary>
public sealed class GoogleOAuthApiFixture : SharedApiFixture
{
    public IGoogleSecretProtector Protector { get; } = new GoogleSecretProtector(
        new EphemeralDataProtectionProvider(), NullLogger<GoogleSecretProtector>.Instance);

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GoogleCalendar:ClientId"] = "client-1",
            ["GoogleCalendar:ClientSecret"] = "secret-1",
        });

        builder.Services.AddSingleton(Protector);
        builder.Services.AddSingleton(new GoogleOAuthClient(
            new HttpClient(new RecordingHttpHandler
            {
                Responder = _ => RecordingHttpHandler.Json(
                    """{"access_token":"at-live","refresh_token":"rt-live","expires_in":3600}"""),
            }),
            "https://accounts.test/authorize",
            "https://oauth2.test/token"));

        // A CONNECTION-AWARE double, deliberately not the bare InMemoryCalendarService. The callback reads
        // the connected identity back through ICalendarService, and production resolves the caller's token
        // from the very grain the callback is writing — so a double that answers regardless of connection
        // state would keep OAuthCallback_PersistsEncryptedTokens green even if the endpoint asked for the
        // identity BEFORE storing the tokens, which is exactly the bug that ordering once had.
        builder.Services.AddSingleton<ICalendarService>(sp =>
            new ConnectionAwareCalendarService(sp.GetRequiredService<IGrainFactory>()));
    }

    /// <summary>
    /// <see cref="InMemoryCalendarService"/>, but only for a user who actually has a stored
    /// <see cref="GoogleConnection"/> — which is what the production adapter does, and what makes the
    /// store-then-resolve ordering in the callback falsifiable.
    /// </summary>
    private sealed class ConnectionAwareCalendarService(IGrainFactory grains) : ICalendarService
    {
        private readonly InMemoryCalendarService _inner = new();

        private async Task<bool> ConnectedAsync(string userId) =>
            await grains.GetGrain<IUserGoogleGrain>(userId).GetAsync() is not null;

        public async Task<CalendarResult<IReadOnlyList<CalendarSummary>>> ListCalendarsAsync(
            string userId, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.ListCalendarsAsync(userId, cancellationToken)
                : CalendarResult<IReadOnlyList<CalendarSummary>>.NotConnected();

        public async Task<CalendarResult<IReadOnlyList<CalendarEvent>>> ListEventsAsync(
            string userId, string? calendarId, DateTimeOffset timeMin, DateTimeOffset timeMax,
            int maxResults, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.ListEventsAsync(userId, calendarId, timeMin, timeMax, maxResults, cancellationToken)
                : CalendarResult<IReadOnlyList<CalendarEvent>>.NotConnected();

        public async Task<CalendarResult<CalendarEvent>> GetEventAsync(
            string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.GetEventAsync(userId, calendarId, eventId, cancellationToken)
                : CalendarResult<CalendarEvent>.NotConnected();

        public async Task<CalendarResult<CalendarEvent>> CreateEventAsync(
            string userId, string? calendarId, CalendarEventDraft draft,
            CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.CreateEventAsync(userId, calendarId, draft, cancellationToken)
                : CalendarResult<CalendarEvent>.NotConnected();

        public async Task<CalendarResult<CalendarEvent>> UpdateEventAsync(
            string userId, string? calendarId, string eventId, CalendarEventDraft draft,
            CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.UpdateEventAsync(userId, calendarId, eventId, draft, cancellationToken)
                : CalendarResult<CalendarEvent>.NotConnected();

        public async Task<CalendarResult<bool>> DeleteEventAsync(
            string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.DeleteEventAsync(userId, calendarId, eventId, cancellationToken)
                : CalendarResult<bool>.NotConnected();
    }
}
