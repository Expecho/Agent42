using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Qualified model ids (ADR 0013). The two properties that matter are the first-colon split — a
/// GitHub Models id contains a slash, so a slash separator would have been ambiguous — and the
/// legacy fallback, which is what lets every conversation persisted before ADR 0013 keep working
/// with no state migration.
/// </summary>
public class ModelIdTests
{
    [Fact] // AC1
    public void ModelId_SplitsOnFirstColon_AndKeepsSlashesInModel()
    {
        var (provider, model) = ModelId.Parse("github:openai/gpt-4o-mini", defaultProvider: "foundry");

        Assert.Equal("github", provider);
        Assert.Equal("openai/gpt-4o-mini", model);
    }

    [Fact] // AC1 — everything after the first colon is the provider's opaque string, colons included.
    public void ModelId_KeepsLaterColonsInTheModelPart()
    {
        var (provider, model) = ModelId.Parse("ollama:llama3.1:8b", defaultProvider: "foundry");

        Assert.Equal("ollama", provider);
        Assert.Equal("llama3.1:8b", model);
    }

    [Fact] // AC2
    public void ModelId_Unqualified_ResolvesToDefaultProvider()
    {
        var (provider, model) = ModelId.Parse("gpt-4o", defaultProvider: "foundry");

        Assert.Equal("foundry", provider);
        Assert.Equal("gpt-4o", model);
    }

    [Fact] // AC1/AC2 — round-trip.
    public void ModelId_Qualify_RoundTripsThroughParse()
    {
        var id = ModelId.Qualify("github", "openai/gpt-4o-mini");
        var (provider, model) = ModelId.Parse(id, defaultProvider: "foundry");

        Assert.Equal("github:openai/gpt-4o-mini", id);
        Assert.Equal("github", provider);
        Assert.Equal("openai/gpt-4o-mini", model);
    }
}
