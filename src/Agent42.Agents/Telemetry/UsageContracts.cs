namespace Agent42.Agents;

// The usage dashboard wire contract (ADR 0031), matching the sibling AI.Telemetry.Dashboard shapes.
// These records both cross the grain boundary (returned by IUsageRollupGrain) AND serialize to the
// browser as camelCase JSON via the endpoint's Results.Json — so they live here in Agents (one
// definition), not as Backend-only DTOs. There is deliberately NO total-tokens field: a total is input+output
// and is the renderer's business. The cost fields (ADR 0110, amending 0031) are DERIVED in Query from the
// checked-in price table and never stored; a null cost means "nothing here could be priced", never $0.

/// <summary>
/// Headline tile figures. <see cref="CachedInputTokens"/> is a SUBSET of <see cref="InputTokens"/>
/// (its own tile, never summed into a total), and is 0 for a provider that reports no cached count
/// (ADR 0031, corrected by ADR 0104).
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UsageTotals")]
public sealed record UsageTotals(
    [property: Id(0)] long InputTokens,
    [property: Id(1)] long OutputTokens,
    [property: Id(2)] long CachedInputTokens,
    [property: Id(3)] long CallCount,
    [property: Id(4)] long ErrorCount,
    [property: Id(5)] long ToolCallCount,
    [property: Id(6)] decimal? CostUsd = null,
    [property: Id(7)] long UnpricedCallCount = 0);

/// <summary>
/// One row of the "tokens by model" table, grouped by (application, provider, model, provider kind). The kind is
/// in the key because it decides the price (ADR 0110); <see cref="CostStatus"/> is one of the
/// <see cref="Agents.CostStatus"/> strings.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ModelUsage")]
public sealed record ModelUsage(
    [property: Id(0)] string Application,
    [property: Id(1)] string Model,
    [property: Id(2)] string Provider,
    [property: Id(3)] long InputTokens,
    [property: Id(4)] long OutputTokens,
    [property: Id(5)] long CachedInputTokens,
    [property: Id(6)] long ReasoningOutputTokens,
    [property: Id(7)] long CallCount,
    [property: Id(8)] long ErrorCount,
    [property: Id(9)] string ProviderKind = "",
    [property: Id(10)] decimal? CostUsd = null,
    [property: Id(11)] string CostStatus = Agents.CostStatus.Unpriced);

/// <summary>One row of the "tool calls by name" table, grouped by (application, tool).</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ToolUsage")]
public sealed record ToolUsage(
    [property: Id(0)] string Application,
    [property: Id(1)] string ToolName,
    [property: Id(2)] long Count,
    [property: Id(3)] long ErrorCount);

/// <summary>One row of the "usage by user" table. Fed by both token and tool events.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UserUsage")]
public sealed record UserUsage(
    [property: Id(0)] string User,
    [property: Id(1)] long InputTokens,
    [property: Id(2)] long OutputTokens,
    [property: Id(3)] long CachedInputTokens,
    [property: Id(4)] long ReasoningOutputTokens,
    [property: Id(5)] long CallCount,
    [property: Id(6)] long ErrorCount,
    [property: Id(7)] long ToolCallCount,
    [property: Id(8)] decimal? CostUsd = null,
    [property: Id(9)] long UnpricedCallCount = 0);

/// <summary>The full <c>GET /api/usage</c> payload: tiles + three tables.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UsageReport")]
public sealed record UsageReport(
    [property: Id(0)] UsageTotals Totals,
    [property: Id(1)] IReadOnlyList<ModelUsage> Models,
    [property: Id(2)] IReadOnlyList<ToolUsage> Tools,
    [property: Id(3)] IReadOnlyList<UserUsage> Users);
