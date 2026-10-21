using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The pure <see cref="UsageRollupState"/> (ADR 0031): bucketing, retention/prune, and the
/// range/filter query — all testable without an Orleans cluster.
/// </summary>
public sealed class UsageRollupTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static UsageEvent Token(
        DateTimeOffset ts, string user = "ada", string model = "gpt-5.4", string provider = "foundry",
        long input = 100, long output = 20, bool error = false, string app = "Agent42")
        => new(UsageEventKind.Token, ts, app, provider, model, user, "", input, output, 0, 0, error);

    private static UsageEvent Tool(
        DateTimeOffset ts, string user = "ada", string tool = "web_search", bool error = false, string app = "Agent42")
        => new(UsageEventKind.Tool, ts, app, "", "", user, tool, 0, 0, 0, 0, error);

    // AC5: repeated events in the same hour/day accumulate into one bucket per grain; call count increments.
    [Fact]
    public void Apply_Accumulates_IntoCorrectBuckets()
    {
        var state = new UsageRollupState();
        state.Apply(Token(Now, input: 100, output: 20));
        state.Apply(Token(Now.AddMinutes(5), input: 50, output: 10));

        var report = state.Query("24h", null, null, Now);

        Assert.Equal(150, report.Totals.InputTokens);
        Assert.Equal(30, report.Totals.OutputTokens);
        Assert.Equal(2, report.Totals.CallCount);
        var model = Assert.Single(report.Models);
        Assert.Equal("gpt-5.4", model.Model);
        Assert.Equal(150, model.InputTokens);
        Assert.Equal(2, model.CallCount);
    }

    // AC7: 24h reads the hourly rollup and excludes events older than 24h; 30d includes them (daily).
    [Fact]
    public void Query_RangeSelectsHourlyOrDaily()
    {
        var state = new UsageRollupState();
        state.Apply(Token(Now.AddHours(-2), input: 10));   // within 24h
        state.Apply(Token(Now.AddDays(-5), input: 100));   // outside 24h, within 30d

        var last24h = state.Query("24h", null, null, Now);
        Assert.Equal(10, last24h.Totals.InputTokens);

        var last30d = state.Query("30d", null, null, Now);
        Assert.Equal(110, last30d.Totals.InputTokens);
    }

    // AC7: application/user filters narrow the result.
    [Fact]
    public void Query_FiltersByApplicationAndUser()
    {
        var state = new UsageRollupState();
        state.Apply(Token(Now, user: "ada", input: 100));
        state.Apply(Token(Now, user: "bob", input: 200));

        var adaOnly = state.Query("30d", null, "ada", Now);
        Assert.Equal(100, adaOnly.Totals.InputTokens);
        Assert.Single(adaOnly.Users);

        var noSuchApp = state.Query("30d", "OtherApp", null, Now);
        Assert.Equal(0, noSuchApp.Totals.InputTokens);
    }

    // AC8: cross-user aggregation — two users produce two user rows plus summed totals.
    [Fact]
    public void Query_AggregatesAcrossUsers()
    {
        var state = new UsageRollupState();
        state.Apply(Token(Now, user: "ada", input: 100, output: 10));
        state.Apply(Token(Now, user: "bob", input: 200, output: 20));
        state.Apply(Tool(Now, user: "bob"));

        var report = state.Query("30d", null, null, Now);

        Assert.Equal(300, report.Totals.InputTokens);
        Assert.Equal(30, report.Totals.OutputTokens);
        Assert.Equal(1, report.Totals.ToolCallCount);
        Assert.Equal(2, report.Users.Count);
        var bob = Assert.Single(report.Users, u => u.User == "bob");
        Assert.Equal(200, bob.InputTokens);
        Assert.Equal(1, bob.ToolCallCount);
        // Sorted by input+output desc → bob first.
        Assert.Equal("bob", report.Users[0].User);
    }

    // AC3/AC8: tool events land in the tools table and contribute tool-call totals, never token totals.
    [Fact]
    public void Query_ToolEvents_FeedToolsTableOnly()
    {
        var state = new UsageRollupState();
        state.Apply(Tool(Now, tool: "web_search"));
        state.Apply(Tool(Now, tool: "web_search", error: true));
        state.Apply(Tool(Now, tool: "location"));

        var report = state.Query("30d", null, null, Now);

        Assert.Equal(0, report.Totals.InputTokens);
        Assert.Equal(3, report.Totals.ToolCallCount);
        Assert.Equal(1, report.Totals.ErrorCount);
        var search = Assert.Single(report.Tools, t => t.ToolName == "web_search");
        Assert.Equal(2, search.Count);
        Assert.Equal(1, search.ErrorCount);
        // Sorted by count desc → web_search first.
        Assert.Equal("web_search", report.Tools[0].ToolName);
    }

    // AC6: Prune drops buckets older than the retention bounds.
    [Fact]
    public void Prune_DropsAgedBuckets()
    {
        var state = new UsageRollupState();
        state.Apply(Token(Now));                                          // fresh
        state.Apply(Token(Now.Subtract(UsageRetention.Daily).AddDays(-1))); // older than daily retention

        // Before prune, the old event is still visible under "all".
        Assert.Equal(200, state.Query("all", null, null, Now).Totals.InputTokens);

        state.Prune(Now);

        // After prune, only the fresh bucket survives.
        Assert.Equal(100, state.Query("all", null, null, Now).Totals.InputTokens);
    }

    // AC9: cached input is never fabricated (OTEL emits none) and there is no total/cost field on the wire.
    [Fact]
    public void Contract_NoCachedFabrication()
    {
        var state = new UsageRollupState();
        state.Apply(Token(Now, input: 100, output: 20));

        var report = state.Query("30d", null, null, Now);

        Assert.Equal(0, report.Totals.CachedInputTokens);
        Assert.Equal(0, report.Models[0].CachedInputTokens);
        Assert.Equal(0, report.Models[0].ReasoningOutputTokens);
        // No 'Total' member exists on UsageTotals — enforced by the type shape. Cost exists since ADR 0110, and is
        // null here because the event carried no provider kind (an unpriced call, not a free one).
        Assert.Null(report.Totals.CostUsd);
        Assert.Equal(1, report.Totals.UnpricedCallCount);
    }

    private static UsageEvent Priced(DateTimeOffset ts, string kind, string model, long input, long output, long cached = 0, string user = "ada")
        => new(UsageEventKind.Token, ts, "Agent42", "openai", model, user, "", input, output, cached, 0, false, kind);

    // ADR 0110: each model row is priced from the table at read time; Copilot is included; an unknown kind or a
    // legacy bucket with no kind is unpriced; totals sum the priced rows and count the unpriced calls.
    [Fact]
    public void Query_ComputesCostPerModelRow()
    {
        var state = new UsageRollupState();
        // gpt-5.4: 2.50 in / 0.25 cached / 15 out. 1M in (400k cached) + 100k out = 1.50 + 0.10 + 1.50 = 3.10
        state.Apply(Priced(Now, ModelPricing.AzureOpenAIKind, "gpt-5.4-2026-03-05", 1_000_000, 100_000, cached: 400_000));
        state.Apply(Priced(Now, ModelPricing.GitHubCopilotKind, "gpt-5.4", 5_000, 500, user: "bob"));
        state.Apply(Priced(Now, "OpenAICompatible", "openai/gpt-4o", 1_000, 100));
        state.Apply(Token(Now, model: "gpt-5.4")); // pre-ADR-0110 shape: no provider kind

        var report = state.Query("24h", null, null, Now);

        var azure = report.Models.Single(m => m.ProviderKind == ModelPricing.AzureOpenAIKind);
        Assert.Equal((CostStatus.Priced, 3.10m), (azure.CostStatus, azure.CostUsd));
        var copilot = report.Models.Single(m => m.ProviderKind == ModelPricing.GitHubCopilotKind);
        Assert.Equal((CostStatus.Included, 0m), (copilot.CostStatus, copilot.CostUsd));
        Assert.All(
            report.Models.Where(m => m.ProviderKind is "" or "OpenAICompatible"),
            m => Assert.Equal((CostStatus.Unpriced, (decimal?)null), (m.CostStatus, m.CostUsd)));

        Assert.Equal(3.10m, report.Totals.CostUsd);
        Assert.Equal(2, report.Totals.UnpricedCallCount);
        var ada = report.Users.Single(u => u.User == "ada");
        Assert.Equal((3.10m, 2L), (ada.CostUsd, ada.UnpricedCallCount));
        Assert.Equal((0m, 0L), (report.Users.Single(u => u.User == "bob").CostUsd, report.Users.Single(u => u.User == "bob").UnpricedCallCount));
    }

    // A row that spans a price's effective date is partial: the calls before the row existed stay unpriced.
    [Fact]
    public void Query_RowSpanningAnEffectiveDate_IsPartial()
    {
        var state = new UsageRollupState();
        var march = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        state.Apply(Priced(march.AddDays(-2), ModelPricing.AzureOpenAIKind, "gpt-5.4", 1_000_000, 0));
        state.Apply(Priced(march, ModelPricing.AzureOpenAIKind, "gpt-5.4", 1_000_000, 0));

        var row = Assert.Single(state.Query("all", null, null, march).Models);

        Assert.Equal((CostStatus.Partial, 2.5m), (row.CostStatus, row.CostUsd));
    }

    // The /usage path has no cache-write term (no OTel tag carries Anthropic cache writes), so all uncached input
    // bills at the input rate. Pinned so the known gap with the turn line is deliberate, not drift.
    [Fact]
    public void Query_AnthropicHasNoCacheWriteTerm()
    {
        var state = new UsageRollupState();
        // claude-sonnet-4-6: 3 in / 0.30 read / 15 out. 1M in (600k read) = 400k x 3 + 600k x 0.30 = 1.20 + 0.18
        state.Apply(Priced(Now, ModelPricing.AnthropicKind, "claude-sonnet-4-6", 1_000_000, 0, cached: 600_000));

        Assert.Equal(1.38m, state.Query("24h", null, null, Now).Totals.CostUsd);
    }

    // Applications/Users list the distinct non-empty dimension values for the dropdowns.
    [Fact]
    public void ListsDistinctDimensionValues()
    {
        var state = new UsageRollupState();
        state.Apply(Token(Now, user: "ada"));
        state.Apply(Token(Now, user: "bob"));
        state.Apply(Token(Now, user: ""));  // unreported — excluded from the dropdown

        Assert.Equal(new[] { "ada", "bob" }, state.Users());
        Assert.Equal(new[] { "Agent42" }, state.Applications());
    }
}
