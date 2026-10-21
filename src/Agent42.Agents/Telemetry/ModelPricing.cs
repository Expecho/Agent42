using System.Text.RegularExpressions;

namespace Agent42.Agents;

/// <summary>
/// The three answers a cost lookup can give (ADR 0110), as the wire strings the browser reads. They are never
/// conflated: <see cref="Unpriced"/> renders "—" and never "$0", because a too-low number is indistinguishable from
/// a genuinely cheap call, and <see cref="Included"/> is a subscription (GitHub Copilot) with no per-token bill.
/// </summary>
public static class CostStatus
{
    public const string Priced = "priced";
    public const string Included = "included";
    public const string Unpriced = "unpriced";

    /// <summary>A <c>/usage</c> aggregate where some calls are priced and some are not, so the amount is a lower bound.</summary>
    public const string Partial = "partial";
}

/// <summary>
/// One effective-dated price row, in USD per 1M tokens (ADR 0110). <see cref="ProviderKind"/> is the Backend's
/// <c>ProviderKind</c> by name (this layer cannot see the enum). A null cached or cache-write rate means "no
/// discounted meter", which bills at the INPUT rate, never at zero.
/// </summary>
public sealed record ModelPrice(
    string ProviderKind,
    string Model,
    DateTimeOffset ValidFrom,
    decimal InputPerMillion,
    decimal? CachedInputPerMillion,
    decimal? CacheWritePerMillion,
    decimal? OutputPerMillion);

/// <summary>
/// One call's (or one turn's) token counts as the provider reported them. <see cref="Input"/> is the provider's
/// whole input count, which already CONTAINS <see cref="CachedInput"/> and, on Anthropic, <see cref="CacheWrite"/>
/// too (spike 2026-10-21, Anthropic 12.58.0: wire input 10 + cache_creation 50 + cache_read 100 → InputTokenCount
/// 160). Reasoning is contained in <see cref="Output"/> and is deliberately not a field.
/// </summary>
public readonly record struct TokenCounts(long Input, long Output, long CachedInput = 0, long CacheWrite = 0);

/// <summary>A cost lookup's answer: a <see cref="CostStatus"/> and, only when priced, the unrounded USD amount.</summary>
public sealed record CostEstimate(string Status, decimal? Usd)
{
    public static CostEstimate Unpriced { get; } = new(CostStatus.Unpriced, null);

    public static CostEstimate Included { get; } = new(CostStatus.Included, null);
}

/// <summary>
/// Estimated cost from a checked-in, effective-dated price table (ADR 0110), ported from the sibling
/// <c>Expecho/GenAI.Telemetry.Dashboard</c> ADR 0012: cost is DERIVED from tokens at read time, never stored, so a
/// price correction recosts history by editing this table. Pure — no clock, no I/O.
///
/// <para>Matching is EXACT after <see cref="Normalize"/>, never by prefix: <c>gpt-5.4-mini</c> must not price as
/// <c>gpt-5.4</c>, and the sibling repo's <c>gpt-4o</c>-matched-the-wrong-meter near-miss is exactly what a prefix
/// match produces silently. A model with no row is unpriced, not free.</para>
///
/// <para>Rows were seeded 2026-10-21. Azure OpenAI rows are the Azure Retail Prices API's <b>Global</b> meters
/// (<c>serviceName eq 'Foundry Models'</c>, Consumption), normalized to per-1M (older meters bill per 1K), dated
/// from the meter's <c>effectiveStartDate</c>; Data Zone and regional deployments price differently, which is why
/// the UI says "estimated". Anthropic rows are first-party list prices (they also apply to Claude on Foundry), with
/// the 5-minute cache write at 1.25× input; their <c>ValidFrom</c> is <see cref="DateTimeOffset.MinValue"/> because
/// only the price in force at seeding is known. OpenAI-compatible endpoints (GitHub Models, Ollama, vLLM) have no
/// rows on purpose: they can be free, local, or anything.</para>
/// </summary>
public static partial class ModelPricing
{
    public const string AzureOpenAIKind = "AzureOpenAI";
    public const string AnthropicKind = "Anthropic";
    public const string GitHubCopilotKind = "GitHubCopilot";

    private const decimal TokensPerPriceUnit = 1_000_000m;

    private static readonly DateTimeOffset Seeded = DateTimeOffset.MinValue;

    /// <summary>The price table. A reprice ADDS a row with a later <see cref="ModelPrice.ValidFrom"/>; it never edits one.</summary>
    public static IReadOnlyList<ModelPrice> Table { get; } =
    [
        // Azure OpenAI, Global meters (USD per 1M: input, cached input, cache write, output).
        Azure("gpt-5", "2025-08-01", 1.25m, 0.125m, 10m),
        Azure("gpt-5-mini", "2025-08-01", 0.25m, 0.025m, 2m),
        Azure("gpt-5-nano", "2025-08-01", 0.05m, 0.005m, 0.4m),
        Azure("gpt-5.1", "2025-11-01", 1.25m, 0.125m, 10m),
        Azure("gpt-5.1-codex", "2025-11-01", 1.25m, 0.125m, 10m),
        Azure("gpt-5.1-codex-mini", "2025-11-01", 0.25m, 0.025m, 2m),
        Azure("gpt-5.1-codex-max", "2025-12-01", 1.25m, 0.125m, 10m),
        Azure("gpt-5.2", "2025-12-01", 1.75m, 0.175m, 14m),
        Azure("gpt-5.2-codex", "2026-01-01", 1.75m, 0.175m, 14m),
        Azure("gpt-5.2-chat", "2026-02-01", 1.75m, 0.175m, 14m),
        Azure("gpt-5.3-chat", "2026-03-01", 1.75m, 0.175m, 14m),
        Azure("gpt-5.3-codex", "2026-03-01", 1.75m, 0.175m, 14m),
        Azure("gpt-5.4", "2026-03-01", 2.5m, 0.25m, 15m),
        Azure("gpt-5.4-mini", "2026-03-01", 0.75m, 0.075m, 4.5m),
        Azure("gpt-5.4-nano", "2026-03-01", 0.2m, 0.02m, 1.25m),
        Azure("gpt-4.1", "2025-04-01", 2m, 0.5m, 8m),
        Azure("gpt-4.1-mini", "2025-04-01", 0.4m, 0.1m, 1.6m),
        Azure("gpt-4.1-nano", "2025-04-01", 0.1m, 0.025m, 0.4m),
        Azure("gpt-4o", "2024-12-01", 2.5m, 1.25m, 10m),
        Azure("gpt-4o-mini", "2024-07-01", 0.15m, 0.075m, 0.6m),
        Azure("o3", "2025-06-01", 2m, 0.5m, 8m),
        Azure("o3-mini", "2025-02-01", 1.1m, 0.55m, 4.4m),
        Azure("o4-mini", "2025-04-01", 1.1m, 0.275m, 4.4m),

        // Anthropic first-party list prices (input, cache read, 5-minute cache write = 1.25x input, output).
        Claude("claude-fable-5-1", 10m, 0.25m, 50m),
        Claude("claude-fable-5", 10m, 1m, 50m),
        Claude("claude-opus-5-5", 4m, 0.2m, 20m),
        Claude("claude-opus-5", 5m, 0.5m, 25m),
        Claude("claude-opus-4-8", 5m, 0.5m, 25m),
        Claude("claude-opus-4-7", 5m, 0.5m, 25m),
        Claude("claude-opus-4-6", 5m, 0.5m, 25m),
        Claude("claude-sonnet-5-5", 2m, 0.2m, 10m),
        Claude("claude-sonnet-5", 2m, 0.2m, 10m),
        Claude("claude-sonnet-4-6", 3m, 0.3m, 15m),
        // Prompts up to 100K tokens; the >100K tier ($0.50/$2.50) is not modelled (ADR 0110).
        Claude("claude-haiku-5-5", 0.1m, 0.01m, 0.5m),
        Claude("claude-haiku-4-5", 1m, 0.1m, 5m),
    ];

    /// <summary>
    /// Lower-cases and strips a trailing release-date suffix (<c>-2024-11-20</c> from Azure, <c>-20251001</c> from
    /// Anthropic), so a dated response model matches its family row. Nothing else is stripped.
    /// </summary>
    public static string Normalize(string? model)
        => DateSuffix().Replace((model ?? "").Trim().ToLowerInvariant(), "");

    /// <summary>The row in force for <paramref name="model"/> at <paramref name="at"/>, or null when there is none.</summary>
    public static ModelPrice? Resolve(string? providerKind, string? model, DateTimeOffset at)
    {
        var key = Normalize(model);
        if (string.IsNullOrEmpty(providerKind) || key.Length == 0)
        {
            return null;
        }

        return Table
            .Where(p => p.ProviderKind == providerKind && p.Model == key && p.ValidFrom <= at)
            .MaxBy(p => p.ValidFrom);
    }

    /// <summary>Prices one call or turn: included for GitHub Copilot, unpriced without a row, else the formula.</summary>
    public static CostEstimate Estimate(string? providerKind, string? model, DateTimeOffset at, TokenCounts tokens)
    {
        if (providerKind == GitHubCopilotKind)
        {
            return CostEstimate.Included;
        }

        var price = Resolve(providerKind, model, at);
        var usd = price is null ? null : Compute(price, tokens);
        return usd is null ? CostEstimate.Unpriced : new CostEstimate(CostStatus.Priced, usd);
    }

    /// <summary>
    /// The money rule. Never rounded: rounding is the renderer's business, and rounding per row then summing is how
    /// totals drift.
    ///
    /// <para>Cached input and cache writes are SUBTRACTED from input, not added: the provider's input count already
    /// contains both, so billing the full input at the standard rate as well would double-charge them. The
    /// subtraction is clamped, so a provider bug reporting more cached than input cannot reduce the bill below the
    /// cached terms. There is no reasoning term: reasoning is part of output.</para>
    ///
    /// <para>Null (unpriceable) when output tokens were reported but the row has no output rate: the table is wrong
    /// for that model, and a plausible input-only number would hide it.</para>
    /// </summary>
    public static decimal? Compute(ModelPrice price, TokenCounts tokens)
    {
        ArgumentNullException.ThrowIfNull(price);

        var input = NonNegative(tokens.Input);
        var output = NonNegative(tokens.Output);
        var cached = NonNegative(tokens.CachedInput);
        var cacheWrite = NonNegative(tokens.CacheWrite);

        if (output > 0 && price.OutputPerMillion is null)
        {
            return null;
        }

        var uncached = Math.Max(0m, input - cached - cacheWrite);
        var cost = (uncached * price.InputPerMillion)
                 + (cached * (price.CachedInputPerMillion ?? price.InputPerMillion))
                 + (cacheWrite * (price.CacheWritePerMillion ?? price.InputPerMillion))
                 + (output * (price.OutputPerMillion ?? 0m));

        return cost / TokensPerPriceUnit;
    }

    private static decimal NonNegative(long tokens) => tokens > 0 ? tokens : 0m;

    private static ModelPrice Azure(string model, string validFrom, decimal input, decimal cached, decimal output)
        => new(AzureOpenAIKind, model, DateTimeOffset.Parse(validFrom + "T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            input, cached, CacheWritePerMillion: null, output);

    private static ModelPrice Claude(string model, decimal input, decimal cacheRead, decimal output)
        => new(AnthropicKind, model, Seeded, input, cacheRead, input * 1.25m, output);

    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2}|\d{8})$")]
    private static partial Regex DateSuffix();
}
