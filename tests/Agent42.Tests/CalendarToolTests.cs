using System.Reflection;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The six calendar tools as the model actually reaches them (ADR 0053): through <c>BuildAgent</c>, into
/// the grain's tool methods, out to <see cref="ICalendarService"/> and back into the turn. The adapter's
/// own wire behaviour is <see cref="GoogleCalendarServiceTests"/>; this file owns the grain half.
/// </summary>
public sealed class CalendarToolTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private IAgentGrain Agent(string grainKey) => Cluster.GrainFactory.GetGrain<IAgentGrain>(grainKey);

    /// <summary>
    /// Runs one turn and returns what the tool actually returned, read off the <c>ToolCallEnd</c> stream
    /// event.
    ///
    /// <para>Deliberately NOT the fake client's <c>[tool:…]</c> echo, which the other tool tests use: that
    /// echo joins <i>every</i> tool result in the conversation and trims the lot to 400 characters, so a
    /// 25-event listing arrives as truncated non-JSON and a third turn's result is pushed out of the
    /// window entirely by the first two. <c>ToolCallEnd</c> carries the whole value, per call.</para>
    /// </summary>
    private static async Task<string> ToolResultAsync(
        IAgentGrain agent, string prompt, bool ephemeral = false)
    {
        string? raw = null;
        await foreach (var evt in agent.ChatAsync(prompt, null, null, null, ephemeral))
        {
            if (evt.Type == AgentStreamEventType.ToolCallEnd && evt.ToolResult is not null)
            {
                raw = evt.ToolResult;
            }
        }

        Assert.NotNull(raw);

        // The grain serializes the tool's return value, and that value is itself a JSON string — so the
        // event carries a JSON-encoded string and one Deserialize gets back to the tool's own JSON.
        return JsonSerializer.Deserialize<string>(raw!) ?? raw!;
    }

    // ── AC1 ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCalendars_ReturnsTheUsersCalendars()
    {
        var result = await ToolResultAsync(Agent("cal-list-user/conv"), "list my calendars please");

        Assert.Contains(InMemoryCalendarService.PrimaryCalendarAddress, result, StringComparison.Ordinal);
        Assert.Contains(InMemoryCalendarService.SecondaryCalendarAddress, result, StringComparison.Ordinal);
    }

    // ── AC3 ─────────────────────────────────────────────────────────────────────────────────────────

    // The full write cycle through the tools. The event id is carried forward in the PROMPT, which is how
    // the fake stands in for a model reading an id out of its own history — without that, create and
    // delete could each "pass" while never referring to the same event.
    [Fact]
    public async Task CreateUpdateDelete_RoundTrips()
    {
        var agent = Agent("cal-roundtrip-user/conv");

        var created = await ToolResultAsync(agent, "book a meeting for lunch");
        var eventId = JsonDocument.Parse(created).RootElement
            .GetProperty("created").GetProperty("Id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(eventId));

        var fetched = await ToolResultAsync(agent, $"get calendar event {eventId} for me");
        Assert.Contains("Lunch with Sam", fetched, StringComparison.Ordinal);

        var updated = await ToolResultAsync(agent, $"update calendar event {eventId} for me");
        Assert.Contains("Lunch with Sam (moved)", updated, StringComparison.Ordinal);

        var deleted = await ToolResultAsync(agent, $"delete calendar event {eventId} for me");
        Assert.Contains("\"deleted\":true", deleted, StringComparison.Ordinal);

        // …and it really is gone: the same id now reads as not found, not as a stale copy.
        var gone = await ToolResultAsync(agent, $"get calendar event {eventId} for me");
        Assert.Contains(CalendarMessages.NotFound, gone, StringComparison.Ordinal);
    }

    // ── AC4 ─────────────────────────────────────────────────────────────────────────────────────────

    // A user with no connection gets a notice the model can act on — never an exception. A throw here
    // would cost the user their whole reply over a tool they may not even have asked for.
    [Fact]
    public async Task NotConnected_ReturnsCuratedNotice_AndDoesNotThrow()
    {
        var agent = Agent($"{FakeCalendarService.NotConnectedUserPrefix}-user/conv");

        var result = await ToolResultAsync(agent, "what's on my calendar this week");

        Assert.Contains(CalendarMessages.NotConnected, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotConnected_WriteAlsoReturnsANotice_AndDoesNotThrow()
    {
        var agent = Agent($"{FakeCalendarService.NotConnectedUserPrefix}-writer/conv");

        var result = await ToolResultAsync(agent, "book a meeting for lunch");

        Assert.Contains(CalendarMessages.NotConnected, result, StringComparison.Ordinal);
    }

    // ── AC5 ─────────────────────────────────────────────────────────────────────────────────────────

    // "List everything this year" is a natural request and exactly the one that would fill the 2 MB
    // session item (ADR 0004). The store is seeded far beyond the cap directly, because seeding it
    // through forty tool calls would test the fake rather than the budget.
    [Fact]
    public async Task ListEvents_RespectsReturnBudget()
    {
        const string userId = "cal-budget-user";
        var seeder = new InMemoryCalendarService();
        var start = DateTimeOffset.UtcNow.AddMinutes(5);

        for (var i = 0; i < CalendarToolLimits.MaxEvents * 2; i++)
        {
            await seeder.CreateEventAsync(userId, null, new CalendarEventDraft(
                Title: $"Seeded {i}",
                Start: start.AddHours(i).ToString("O"),
                End: start.AddHours(i).AddMinutes(30).ToString("O")));
        }

        var result = await ToolResultAsync(Agent($"{userId}/conv"), "what's on my calendar");
        var events = JsonDocument.Parse(result).RootElement.GetProperty("events");

        Assert.True(
            events.GetArrayLength() <= CalendarToolLimits.MaxEvents,
            $"list_calendar_events returned {events.GetArrayLength()} events, over the "
                + $"{CalendarToolLimits.MaxEvents} budget — this value is persisted into the 2 MB session.");
    }

    // ── AC18 ────────────────────────────────────────────────────────────────────────────────────────

    // Derived decision (a): the credential never crosses the port. Pinned by assertion rather than by
    // comment, the GetCurrentLocation_ExposesNoParameters discipline — a later contributor adding an
    // `accessToken` parameter "just for the adapter" would otherwise pass review.
    [Fact]
    public void CalendarPort_ExposesNoCredential()
    {
        string[] forbidden =
            ["token", "secret", "credential", "password", "apikey", "bearer", "auth", "cipher"];

        var offenders = new List<string>();

        foreach (var method in typeof(ICalendarService).GetMethods())
        {
            offenders.AddRange(method.GetParameters()
                // A CancellationToken is not a credential, and it is excluded by TYPE rather than by
                // name so that adding "token" back to the forbidden list cannot be defeated by calling
                // a real credential `ct`.
                .Where(p => p.ParameterType != typeof(CancellationToken) && Suspicious(p.Name))
                .Select(p => $"{method.Name}({p.Name})"));
        }

        // …and the same for every member of the records the tools hand back, which is the other half of
        // the surface: a token smuggled onto a DTO reaches the transcript, not just the port.
        foreach (var type in new[] { typeof(CalendarSummary), typeof(CalendarEvent), typeof(CalendarEventDraft) })
        {
            offenders.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => Suspicious(p.Name))
                .Select(p => $"{type.Name}.{p.Name}"));
        }

        Assert.True(
            offenders.Count == 0,
            "No credential may cross ICalendarService — the adapter resolves and decrypts one itself "
                + "(ADR 0053, derived decision (a)). Offenders: " + string.Join(", ", offenders));

        bool Suspicious(string? name) => name is not null
            && forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    // ── AC20 ────────────────────────────────────────────────────────────────────────────────────────

    // An incognito chat (ADR 0034) may still write to the calendar, DELIBERATELY. ADR 0034 blocks
    // documents and refuses scheduling because both create durable records inside OUR system; a calendar
    // event's durable record is outside our system and is the user's own explicit intent, exactly like a
    // web search leaving the tenant. This test exists so the non-refusal is not later "fixed" by analogy
    // with SchedulePromptAsync — if you are here because you were about to add that refusal, read ADR 0053.
    [Fact]
    public async Task EphemeralChat_MayStillWriteToTheCalendar()
    {
        var agent = Agent("cal-incognito-user/conv");

        var result = await ToolResultAsync(agent, "book a meeting for lunch", ephemeral: true);

        Assert.DoesNotContain(CalendarMessages.NotConnected, result, StringComparison.Ordinal);
        var created = JsonDocument.Parse(result).RootElement.GetProperty("created");
        Assert.Equal("Lunch with Sam", created.GetProperty("Title").GetString());

        // The event really is on the calendar, not merely reported as created.
        var stored = await new InMemoryCalendarService().GetEventAsync(
            "cal-incognito-user", null, created.GetProperty("Id").GetString()!);
        Assert.Equal(CalendarOutcome.Ok, stored.Outcome);
    }

    // ── AC21 ────────────────────────────────────────────────────────────────────────────────────────

    // Derived decision (g): listing expands recurrences, so an id names ONE occurrence and an edit or
    // delete cannot touch the series. The model only knows that if the tool descriptions say so, and a
    // model that does not know it will cheerfully claim it rescheduled a weekly meeting.
    //
    // Asserted on the source text because a registered AIFunction's description is not reachable from
    // outside BuildAgent — the same source-text discipline as the frontend asset guards.
    [Fact]
    public void CalendarToolDescriptions_StateTheSingleInstanceLimitation()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Agent42.Agents", "Tools", "AgentGrain.BuildAgent.cs"));

        foreach (var tool in new[] { "list_calendar_events", "update_calendar_event", "delete_calendar_event" })
        {
            var description = DescriptionOf(source, tool);

            Assert.True(
                description.Contains("expand", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("occurrence", StringComparison.OrdinalIgnoreCase),
                $"The description of {tool} must tell the model that listing expands recurring events, "
                    + "so an id names one occurrence and not the series (ADR 0053, derived decision (g)). "
                    + $"It currently reads: {description}");
        }
    }

    // Everything between a tool's name: and the closing paren of its AIFunctionFactory.Create call.
    private static string DescriptionOf(string source, string toolName)
    {
        var at = source.IndexOf($"name: \"{toolName}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{toolName} is not registered in BuildAgent.");

        var end = source.IndexOf(");", at, StringComparison.Ordinal);
        return source[at..end];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
