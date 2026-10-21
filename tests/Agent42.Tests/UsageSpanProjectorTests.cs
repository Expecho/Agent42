using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The pure span→<see cref="UsageEvent"/> projection (ADR 0031). The load-bearing rule (spike-verified):
/// count tokens ONLY from <c>chat</c> spans and tool events ONLY from <c>execute_tool</c> spans —
/// <c>orchestrate_tools</c> re-reports the SUM of its child chat spans, so counting it double-counts.
/// The Backend processor adapter is covered by <see cref="UsageProcessorTests"/>.
/// </summary>
public sealed class UsageSpanProjectorTests
{
    private const string App = "Agent42";
    private static readonly DateTimeOffset When = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, object?> Tags(params (string Key, object? Value)[] tags)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in tags)
        {
            map[key] = value;
        }

        return map;
    }

    // AC11b (ADR 0074): an identity alias does NOT merge one person's two accounts into one usage row.
    // The user bucket is resolved user.email -> user.name -> user.id, and in production the email is
    // always present, so aliasing user.id never reaches this projection. ADR 0074 records that as a
    // consequence rather than a benefit; this is what keeps the claim falsifiable.
    [Fact]
    public void ChatSpan_WithAnAliasedUserId_StillBucketsOnTheRealEmail()
    {
        var tags = Tags(
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.usage.input_tokens", 10),
            ("gen_ai.usage.output_tokens", 5),
            ("gen_ai.response.model", "gpt-5.4"),
            ("gen_ai.provider.name", "foundry"),
            // The alias has already rewritten user.id to the canonical account's object id...
            ("user.id", "2da7fa48-e41e-4ebd-b665-96bc4210fefd"),
            // ...but user.name/user.email are never aliased, so the span still names who signed in.
            ("user.email", "msn@2000miles.nl"));

        var e = UsageSpanProjector.Project(tags, When, activityErrored: false, App);

        Assert.NotNull(e);
        Assert.Equal("msn@2000miles.nl", e!.UserKey);
    }

    // AC1: a chat span projects to a token event carrying input/output/model/provider/user.
    [Fact]
    public void ChatSpan_ProjectsTokenEvent()
    {
        var tags = Tags(
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.usage.input_tokens", 100),
            ("gen_ai.usage.output_tokens", 42),
            ("gen_ai.response.model", "gpt-5.4"),
            ("gen_ai.provider.name", "foundry"),
            ("user.email", "ada@example.com"));

        var e = UsageSpanProjector.Project(tags, When, activityErrored: false, App);

        Assert.NotNull(e);
        Assert.Equal(UsageEventKind.Token, e!.Kind);
        Assert.Equal(100, e.InputTokens);
        Assert.Equal(42, e.OutputTokens);
        Assert.Equal("gpt-5.4", e.Model);
        Assert.Equal("foundry", e.Provider);
        Assert.Equal("ada@example.com", e.UserKey);
        Assert.Equal(App, e.Application);
        Assert.False(e.IsError);
    }

    // AC1: model falls back to request.model when response.model is absent.
    [Fact]
    public void ChatSpan_FallsBackToRequestModel()
    {
        var tags = Tags(
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.request.model", "gpt-req"),
            ("gen_ai.usage.input_tokens", 1),
            ("gen_ai.usage.output_tokens", 1));

        var e = UsageSpanProjector.Project(tags, When, false, App);
        Assert.Equal("gpt-req", e!.Model);
    }

    // ADR 0110: the provider-kind tag rides into the event; a span without it (any span before the tag existed)
    // projects "", which prices as unpriced.
    [Fact]
    public void ChatSpan_CarriesProviderKind()
    {
        var tagged = UsageSpanProjector.Project(
            Tags(("gen_ai.operation.name", "chat"), (UsageSpanProjector.ProviderKindTag, "AzureOpenAI")), When, false, App);
        var untagged = UsageSpanProjector.Project(Tags(("gen_ai.operation.name", "chat")), When, false, App);

        Assert.Equal("AzureOpenAI", tagged!.ProviderKind);
        Assert.Equal("", untagged!.ProviderKind);
    }

    // AC2: orchestrate_tools (and any non-chat/non-tool op) is IGNORED — the double-count guard.
    [Theory]
    [InlineData("orchestrate_tools")]
    [InlineData("invoke_agent")]
    [InlineData("something_else")]
    public void AggregateSpan_IsNotCounted(string operation)
    {
        var tags = Tags(
            ("gen_ai.operation.name", operation),
            ("gen_ai.usage.input_tokens", 999),
            ("gen_ai.usage.output_tokens", 999));

        Assert.Null(UsageSpanProjector.Project(tags, When, false, App));
    }

    // AC3: an execute_tool span projects to a tool event with a name and no tokens.
    [Fact]
    public void ToolSpan_ProjectsToolEvent()
    {
        var tags = Tags(
            ("gen_ai.operation.name", "execute_tool"),
            ("gen_ai.tool.name", "web_search"),
            ("user.email", "ada@example.com"));

        var e = UsageSpanProjector.Project(tags, When, false, App);

        Assert.NotNull(e);
        Assert.Equal(UsageEventKind.Tool, e!.Kind);
        Assert.Equal("web_search", e.ToolName);
        Assert.Equal("ada@example.com", e.UserKey);
        Assert.Equal(0, e.InputTokens);
        Assert.Equal(0, e.OutputTokens);
    }

    // AC4: missing model/provider/user coalesce to empty ("unreported") — never null.
    [Fact]
    public void MissingDimensions_CoalesceToUnreported()
    {
        var tags = Tags(
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.usage.input_tokens", 5),
            ("gen_ai.usage.output_tokens", 5));

        var e = UsageSpanProjector.Project(tags, When, false, App);

        Assert.Equal("", e!.Model);
        Assert.Equal("", e.Provider);
        Assert.Equal("", e.UserKey);
    }

    // AC1: an error.type tag flags the event as errored even when the activity status isn't error.
    [Fact]
    public void ErrorTypeTag_FlagsError()
    {
        var tags = Tags(
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.usage.input_tokens", 1),
            ("gen_ai.usage.output_tokens", 0),
            ("error.type", "timeout"));

        var e = UsageSpanProjector.Project(tags, When, activityErrored: false, App);
        Assert.True(e!.IsError);
    }

    // User identity prefers email, then name, then id.
    [Fact]
    public void UserKey_PrefersEmailThenNameThenId()
    {
        var byName = UsageSpanProjector.Project(
            Tags(("gen_ai.operation.name", "chat"), ("user.name", "Ada"), ("user.id", "oid-1")), When, false, App);
        Assert.Equal("Ada", byName!.UserKey);

        var byId = UsageSpanProjector.Project(
            Tags(("gen_ai.operation.name", "chat"), ("user.id", "oid-1")), When, false, App);
        Assert.Equal("oid-1", byId!.UserKey);
    }

    // Per-agent application: a stamped agent42.application tag overrides the caller's default constant
    // (for both token and tool events); its absence falls back to the default.
    [Fact]
    public void ApplicationTag_OverridesDefault_ElseFallsBack()
    {
        var withAgent = UsageSpanProjector.Project(
            Tags(("gen_ai.operation.name", "chat"), ("agent42.application", "WeatherAgent"),
                ("gen_ai.usage.input_tokens", 1), ("gen_ai.usage.output_tokens", 1)),
            When, false, App);
        Assert.Equal("WeatherAgent", withAgent!.Application);

        var toolWithAgent = UsageSpanProjector.Project(
            Tags(("gen_ai.operation.name", "execute_tool"), ("agent42.application", "WeatherAgent"),
                ("gen_ai.tool.name", "web_search")),
            When, false, App);
        Assert.Equal("WeatherAgent", toolWithAgent!.Application);

        var noAgent = UsageSpanProjector.Project(
            Tags(("gen_ai.operation.name", "chat"), ("gen_ai.usage.input_tokens", 1), ("gen_ai.usage.output_tokens", 1)),
            When, false, App);
        Assert.Equal(App, noAgent!.Application);
    }
}
