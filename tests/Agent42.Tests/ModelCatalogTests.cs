using Agent42.Backend;
using Azure.Core;
using Azure.ResourceManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The composite catalog (ADR 0013). The ARM-querying half can't run offline, so a fake stands in for
/// a discovering Foundry provider — what is under test here is the composition: qualified ids, provider
/// tagging, capability flags, and degradation when one provider fails.
/// </summary>
public class ModelCatalogTests
{
    [Fact] // AC6
    public async Task Catalog_ReturnsQualifiedIds_WithProviderAndCapabilities()
    {
        var catalog = new CompositeModelCatalog(
            [Discovering("foundry", "gpt-4o")],
            [new ModelInfo("github:openai/gpt-4o-mini", "github", "GPT-4o mini", "openai/gpt-4o-mini",
                null, SupportsTools: true, SupportsVision: false)],
            "foundry:gpt-4o",
            NullLogger<CompositeModelCatalog>.Instance);

        var result = await catalog.GetAsync();

        var foundry = Assert.Single(result.Models, m => m.Provider == "foundry");
        Assert.Equal("foundry:gpt-4o", foundry.Id);

        var github = Assert.Single(result.Models, m => m.Provider == "github");
        Assert.Equal("github:openai/gpt-4o-mini", github.Id);
        Assert.True(github.SupportsTools);
        Assert.False(github.SupportsVision);
    }

    [Fact] // AC16 — the collision qualified ids exist to solve.
    public async Task TwoFoundryProviders_YieldDistinctQualifiedIds()
    {
        // The SAME bare deployment name on two different Foundry accounts.
        var catalog = new CompositeModelCatalog(
            [Discovering("foundry", "gpt-4o"), Discovering("foundry-swe", "gpt-4o")],
            [],
            "foundry:gpt-4o",
            NullLogger<CompositeModelCatalog>.Instance);

        var result = await catalog.GetAsync();

        Assert.Equal(
            ["foundry:gpt-4o", "foundry-swe:gpt-4o"],
            result.Models.Select(m => m.Id).Order().Reverse());
    }

    [Fact] // A failing provider contributes nothing rather than emptying the picker.
    public async Task FailingProvider_IsSkipped_AndTheRestStillList()
    {
        var catalog = new CompositeModelCatalog(
            [new ThrowingCatalog(), Discovering("foundry", "gpt-4o")],
            [],
            "foundry:gpt-4o",
            NullLogger<CompositeModelCatalog>.Instance);

        var result = await catalog.GetAsync();

        Assert.Equal("foundry:gpt-4o", Assert.Single(result.Models).Id);
    }

    [Fact] // The picker is never blank: with every provider empty, the default is still offered.
    public async Task NoProviderListsAnything_TheDefaultIsStillOffered()
    {
        var catalog = new CompositeModelCatalog(
            [], [], "foundry:gpt-4o", NullLogger<CompositeModelCatalog>.Instance);

        var result = await catalog.GetAsync();

        Assert.Equal("foundry:gpt-4o", Assert.Single(result.Models).Id);
    }

    [Fact] // AC4 (ADR 0019) — an EMPTY default (the user has configured no chat provider) yields an EMPTY
           // catalog, NOT a synthesized "" model: the picker stays empty so the composer gates on it.
    public async Task EmptyDefault_ReturnsNoModels()
    {
        var catalog = new CompositeModelCatalog(
            [], [], "", NullLogger<CompositeModelCatalog>.Instance);

        var result = await catalog.GetAsync();

        Assert.Empty(result.Models);
        Assert.Equal("", result.Default);
    }

    // The REAL ARM query path, driven by a canned transport — the composite tests above use a static
    // stand-in, which proves the composition but never the ARM→ModelInfo mapping. This is the mapping
    // that has to be right for a second Foundry account to work at all.
    [Fact] // AC6 + AC16
    public async Task AzureCatalog_QualifiesDeploymentsUnderItsOwnProvider_AndQueriesItsOwnAccount()
    {
        var transport = new CannedArmTransport("gpt-4o", "gpt-4o-mini");

        var catalog = new AzureModelCatalog(
            Arm(transport),
            "foundry-swe",
            // The subscription id must be a real GUID: ResourceIdentifier validates it and throws
            // FormatException otherwise — which AzureModelCatalog catches, so a typo'd subscription in
            // config degrades to "this provider lists nothing" rather than failing loudly.
            // ApiKey (2nd field) is irrelevant to ARM discovery — the ARM catalog reads only endpoint/coords.
            new FoundrySettings("https://sweden.openai.azure.com", null, SecondSubscription, "rg-2", "account-2"),
            "foundry:gpt-4o",
            NullLogger<AzureModelCatalog>.Instance);

        var result = await catalog.GetAsync();

        // Every deployment is qualified under THIS provider — not the default one, and not bare. Getting
        // this wrong is how a second account silently serves the first account's models under its name.
        Assert.Equal(["foundry-swe:gpt-4o", "foundry-swe:gpt-4o-mini"], result.Models.Select(m => m.Id));
        Assert.All(result.Models, m => Assert.Equal("foundry-swe", m.Provider));
        Assert.All(result.Models, m => Assert.True(m.SupportsTools && m.SupportsVision));
        Assert.Equal("2024-08-06", result.Models[0].Version);

        // ...and it queried ITS OWN account, not the one in the global AzureOpenAI:* keys.
        Assert.Contains(transport.RequestedUris, u => u.Contains("rg-2") && u.Contains("account-2"));
        Assert.DoesNotContain(transport.RequestedUris, u => u.Contains("account-1"));
    }

    // A Foundry provider whose ARM coordinates are neither declared nor inheritable contributes nothing
    // and warns, rather than throwing or hanging the picker. (The ARM query itself is prod-only — see
    // "Not covered offline" in the spec; this is the one branch of it that is pure logic.)
    [Fact]
    public async Task FoundryProvider_WithoutCoordinates_ContributesNothing()
    {
        var catalog = new AzureModelCatalog(
            new ArmClient(new StaticTokenCredential()),
            "foundry",
            new FoundrySettings("https://example.openai.azure.com", null, null, null, null),
            "foundry:gpt-4o",
            NullLogger<AzureModelCatalog>.Instance);

        var result = await catalog.GetAsync();

        Assert.Empty(result.Models);
    }

    private const string SecondSubscription = "22222222-2222-2222-2222-222222222222";

    private static ArmClient Arm(CannedArmTransport transport) => new(
        new StaticTokenCredential(),
        defaultSubscriptionId: SecondSubscription,
        options: new ArmClientOptions { Transport = transport });

    private static IModelCatalog Discovering(string provider, string deployment) =>
        new StaticModelCatalog($"{provider}:{deployment}",
        [
            new ModelInfo($"{provider}:{deployment}", provider, deployment, deployment, "2024-08-06",
                SupportsTools: true, SupportsVision: true),
        ]);

    private sealed class ThrowingCatalog : IModelCatalog
    {
        public Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ARM is having a day.");
    }
}
