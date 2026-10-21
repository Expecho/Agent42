using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The pure price table and cost formula (ADR 0110). Every expected amount is worked out by hand from the
/// table row the test names, so a table edit that changes a price fails here loudly.
/// </summary>
public sealed class ModelPricingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    // Azure: cached is a SUBSET of input and billed at the cached rate; output at the output rate; no reasoning term.
    [Fact]
    public void Compute_AzureCachedSubsetOfInput()
    {
        // gpt-5.4: 2.50 in / 0.25 cached / 15 out per 1M.
        var estimate = ModelPricing.Estimate(
            ModelPricing.AzureOpenAIKind, "gpt-5.4", Now, new TokenCounts(Input: 1_000_000, Output: 100_000, CachedInput: 400_000));

        Assert.Equal(CostStatus.Priced, estimate.Status);
        // 600k x 2.50 + 400k x 0.25 + 100k x 15 = 1.50 + 0.10 + 1.50
        Assert.Equal(3.10m, estimate.Usd);
    }

    // Anthropic: the provider's input count contains cache reads AND cache writes (spike 2026-10-21).
    [Fact]
    public void Compute_AnthropicInputIncludesCacheReadAndWrite()
    {
        // claude-sonnet-4-6: 3 in / 0.30 read / 3.75 write / 15 out per 1M. Input 1.6M = 1.0M read + 0.5M write + 0.1M plain.
        var estimate = ModelPricing.Estimate(
            ModelPricing.AnthropicKind, "claude-sonnet-4-6", Now,
            new TokenCounts(Input: 1_600_000, Output: 200_000, CachedInput: 1_000_000, CacheWrite: 500_000));

        // 100k x 3 + 1M x 0.30 + 500k x 3.75 + 200k x 15 = 0.30 + 0.30 + 1.875 + 3.00
        Assert.Equal(5.475m, estimate.Usd);
    }

    [Fact]
    public void Compute_NoCachedRate_BillsAtInputRate()
    {
        var price = new ModelPrice("AzureOpenAI", "x", DateTimeOffset.MinValue, 2m, null, null, 8m);

        Assert.Equal(2m, ModelPricing.Compute(price, new TokenCounts(Input: 1_000_000, Output: 0, CachedInput: 1_000_000)));
    }

    [Fact]
    public void Compute_OutputWithoutOutputRate_IsUnpriceable()
    {
        var price = new ModelPrice("AzureOpenAI", "embed", DateTimeOffset.MinValue, 0.13m, null, null, null);

        Assert.Null(ModelPricing.Compute(price, new TokenCounts(Input: 10, Output: 5)));
        Assert.Equal(0.0000013m, ModelPricing.Compute(price, new TokenCounts(Input: 10, Output: 0)));
    }

    // A provider bug reporting more cached than input must not produce a negative uncached term.
    [Fact]
    public void Compute_CachedAboveInput_IsClamped()
    {
        var price = ModelPricing.Resolve(ModelPricing.AzureOpenAIKind, "gpt-5.4", Now)!;

        Assert.Equal(0.25m, ModelPricing.Compute(price, new TokenCounts(Input: 500_000, Output: 0, CachedInput: 1_000_000)));
    }

    [Fact]
    public void Resolve_CopilotIsIncluded()
    {
        var estimate = ModelPricing.Estimate(ModelPricing.GitHubCopilotKind, "gpt-5.4", Now, new TokenCounts(1000, 100));

        Assert.Equal(CostStatus.Included, estimate.Status);
        Assert.Null(estimate.Usd);
    }

    [Theory]
    [InlineData("OpenAICompatible", "gpt-4o")] // GitHub Models / Ollama: no rows on purpose
    [InlineData("AzureOpenAI", "my-deployment")] // a deployment name that is not a model name
    [InlineData("AzureOpenAI", "")]
    [InlineData("", "gpt-5.4")] // a legacy bucket from before the provider-kind tag
    [InlineData("Anthropic", "gpt-5.4")] // the kind is part of the key
    public void Resolve_UnknownIsUnpriced(string kind, string model)
    {
        var estimate = ModelPricing.Estimate(kind, model, Now, new TokenCounts(1000, 100));

        Assert.Equal(CostStatus.Unpriced, estimate.Status);
        Assert.Null(estimate.Usd);
    }

    [Theory]
    [InlineData("gpt-4o-2024-11-20", "gpt-4o")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5")]
    [InlineData(" GPT-5.4 ", "gpt-5.4")]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("o4-mini", "o4-mini")]
    public void Normalize_StripsDateSuffix(string model, string expected)
        => Assert.Equal(expected, ModelPricing.Normalize(model));

    // Exact match after normalization: a variant without its own row is unpriced, never priced as its family.
    [Fact]
    public void Resolve_NoPrefixMatch()
    {
        Assert.Equal(0.75m, ModelPricing.Resolve(ModelPricing.AzureOpenAIKind, "gpt-5.4-mini-2026-03-17", Now)!.InputPerMillion);
        Assert.Null(ModelPricing.Resolve(ModelPricing.AzureOpenAIKind, "gpt-5.4-pro", Now));
        Assert.Null(ModelPricing.Resolve(ModelPricing.AzureOpenAIKind, "gpt-5.4o", Now));
    }

    [Fact]
    public void Resolve_PicksRowInForceAtTime()
    {
        // gpt-5.4's row is dated 2026-03-01: before that it has no price.
        Assert.Null(ModelPricing.Resolve(ModelPricing.AzureOpenAIKind, "gpt-5.4", new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero)));
        Assert.NotNull(ModelPricing.Resolve(ModelPricing.AzureOpenAIKind, "gpt-5.4", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    // Guards the table itself: one row per (kind, model, validFrom), and every model key is already normalized,
    // or Resolve could never match it.
    [Fact]
    public void Table_KeysAreUniqueAndNormalized()
    {
        Assert.Equal(
            ModelPricing.Table.Count,
            ModelPricing.Table.Select(p => (p.ProviderKind, p.Model, p.ValidFrom)).Distinct().Count());
        Assert.All(ModelPricing.Table, p => Assert.Equal(ModelPricing.Normalize(p.Model), p.Model));
        Assert.All(ModelPricing.Table, p => Assert.NotNull(p.OutputPerMillion));
    }
}
