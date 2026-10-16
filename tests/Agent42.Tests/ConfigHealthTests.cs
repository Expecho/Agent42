using System.Text.Json;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent42.Tests;

/// <summary>
/// The <see cref="ConfigHealth"/> static builder and live-probe merge (Preferences → Configuration). The
/// load-bearing property under test is the never-leak invariant — on BOTH paths: a static report built from
/// fake secrets, and a failing secret-bearing PROBE (the geocoding key rides in the request query string, so
/// an unsanitized probe exception would leak it). Everything asserts at the SERIALIZED-response level, the
/// same bytes the endpoint returns.
/// </summary>
public sealed class ConfigHealthTests
{
    private const string Environment = "Production";

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static IHostEnvironment Env(string name = Environment) => new FakeHostEnvironment(name);

    private static PushConfig Push(bool enabled = true) => new(enabled, enabled ? "pub" : "", "mailto:x@y");

    // Every secret the app knows about, all set to a recognisable sentinel — so a leak test can look for ANY
    // of these strings in the serialized report.
    private static readonly (string Key, string? Value)[] AllSecretsSet =
    [
        ("AzureOpenAI:Endpoint", "https://acct.services.ai.azure.com"),
        ("AzureOpenAI:ApiKey", "SECRET-openai-key"),
        ("AzureOpenAI:TenantId", "11111111-1111-1111-1111-111111111111"),
        ("WebSearch:Endpoint", "http://localhost:8888"),
        ("Storage:BlobEndpoint", "https://acct.blob.core.windows.net"),
        ("Geocoding:GoogleApiKey", "SECRET-google-key"),
        ("ContextScrape:ApiKey", "SECRET-context-key"),
        ("Hindsight:ApiKey", "SECRET-hindsight-key"),
        ("Email:MailgunSigningKey", "SECRET-mailgun-signing-key"),
        ("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=SECRET-appinsights-key;IngestionEndpoint=https://x/"),
    ];

    private static ConfigCheck Find(ConfigHealthReport report, string key) =>
        report.Checks.Single(c => c.Key == key);

    // ————— Identity aliases (ADR 0074) —————

    // An active alias reports ok and says how many, so an operator can tell the feature is on.
    [Fact]
    public void IdentityAliases_WhenConfigured_ReportOkWithACount()
    {
        var report = ConfigHealth.BuildStatic(
            Config(($"{UserIdAliases.SectionName}:11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222")),
            Env(),
            Push());

        var check = Find(report, UserIdAliases.SectionName);
        Assert.Equal(ConfigHealth.Ok, check.Status);
        Assert.Contains("1 alias", check.Message, StringComparison.Ordinal);
    }

    // The signal this check exists for: an entry refused at bind time is otherwise indistinguishable
    // from the feature never having shipped — the account just sees an empty dataset.
    [Fact]
    public void IdentityAliases_WhenAnEntryIsInvalid_ReportsAWarningAndNeverEchoesAnObjectId()
    {
        const string aliasOid = "11111111-1111-1111-1111-111111111111";
        var report = ConfigHealth.BuildStatic(
            Config(($"{UserIdAliases.SectionName}:{aliasOid}", "not-a-guid")),
            Env(),
            Push());

        var check = Find(report, UserIdAliases.SectionName);
        Assert.Equal(ConfigHealth.Warning, check.Status);
        Assert.DoesNotContain(aliasOid, check.Message, StringComparison.OrdinalIgnoreCase);
    }

    // No aliases is the default and is healthy, not a warning.
    [Fact]
    public void IdentityAliases_WhenAbsent_ReportOk()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push());

        Assert.Equal(ConfigHealth.Ok, Find(report, UserIdAliases.SectionName).Status);
    }

    // AC2: all configured → the optional items and the fatal core summarise to ok.
    [Fact]
    public void AllConfigured_ReportsOk()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));

        // Embeddings are no longer operator config (AzureOpenAI:ApiKey/EmbeddingDeploymentName floor removed —
        // RAG is a per-user model now, ADR 0007/0018), so there is no AzureOpenAI Core check to assert here.
        Assert.Equal(ConfigHealth.Ok, Find(report, "WebSearch:Endpoint").Status);
        Assert.Equal(ConfigHealth.Ok, Find(report, "Geocoding:GoogleApiKey").Status);
        Assert.Equal(ConfigHealth.Ok, Find(report, "ContextScrape:ApiKey").Status);
        Assert.Equal(ConfigHealth.Ok, Find(report, "Push:Vapid").Status);
        Assert.Equal(ConfigHealth.Ok, Find(report, "Hindsight:ApiKey").Status);
    }

    // AC1: a missing optional secret is a warning, not an error (the app boots fine without it).
    [Fact]
    public void MissingOptionalSecret_IsWarning()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet.Where(e => e.Key != "Geocoding:GoogleApiKey").ToArray()),
            Env(), Push(enabled: true));

        Assert.Equal(ConfigHealth.Warning, Find(report, "Geocoding:GoogleApiKey").Status);
    }

    // ADR 0096: the Mailgun signing key is a presence row. Missing is a warning that names the 503, not a degradation.
    [Fact]
    public void MailgunSigningKey_PresenceRow_NamesThe503WhenMissing()
    {
        var configured = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));
        var without = ConfigHealth.BuildStatic(
            Config(AllSecretsSet.Where(e => e.Key != "Email:MailgunSigningKey").ToArray()), Env(), Push(enabled: true));

        Assert.Equal(ConfigHealth.Ok, Find(configured, "Email:MailgunSigningKey").Status);
        var missing = Find(without, "Email:MailgunSigningKey");
        Assert.Equal(ConfigHealth.Warning, missing.Status);
        Assert.Contains("503", missing.Message, StringComparison.Ordinal);
    }

    // AC3: a malformed endpoint is an error (present but not an absolute URL).
    [Fact]
    public void MalformedEndpoint_IsError()
    {
        var entries = AllSecretsSet.Select(e => e.Key == "WebSearch:Endpoint" ? (e.Key, "not-a-url") : e).ToArray();
        var report = ConfigHealth.BuildStatic(Config(entries), Env(), Push(enabled: true));

        Assert.Equal(ConfigHealth.Error, Find(report, "WebSearch:Endpoint").Status);
    }

    // AC5: push status mirrors PushConfig.Enabled — never recomputed from raw keys.
    [Theory]
    [InlineData(true, ConfigHealth.Ok)]
    [InlineData(false, ConfigHealth.Warning)]
    public void PushCheck_MirrorsPushConfigEnabled(bool enabled, string expected)
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled));

        Assert.Equal(expected, Find(report, "Push:Vapid").Status);
    }

    // AC7: secrets the app genuinely cannot see are reported skipped WITH a reason, not faked as ok/error.
    [Fact]
    public void UnreadableSecret_IsSkippedWithReason()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));

        var searxng = Find(report, "SEARXNG_SECRET");
        Assert.Equal(ConfigHealth.Skipped, searxng.Status);
        Assert.False(string.IsNullOrWhiteSpace(searxng.Message));

        var aad = Find(report, "AAD_CLIENT_SECRET");
        Assert.Equal(ConfigHealth.Skipped, aad.Status);
        Assert.False(string.IsNullOrWhiteSpace(aad.Message));
    }

    // AC4: the STATIC report never contains a secret value — even with every secret set to a sentinel, none
    // of them appear in the serialized bytes.
    [Fact]
    public void Report_NeverContainsSecretValues()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));
        var json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain("SECRET-openai-key", json);
        Assert.DoesNotContain("SECRET-google-key", json);
        Assert.DoesNotContain("SECRET-context-key", json);
    }

    // AC4b (the HIGH finding): a failing secret-bearing PROBE never leaks the secret. The fake geocoding
    // probe THROWS an exception whose text contains the key (as a real HttpRequestException carrying the
    // request URI would); ProbeAsync must catch it and emit a curated message — the key must be absent from
    // the serialized report.
    [Fact]
    public async Task FailingProbe_NeverLeaksSecret()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));
        var probes = new ConfigHealthProbeSet(
            SearXng: null,
            Geocoding: _ => throw new InvalidOperationException(
                "GET https://maps.googleapis.com/maps/api/geocode/json?latlng=0,0&key=SECRET-google-key failed"),
            ContextScrape: null,
            Hindsight: null);
        var services = new ServiceCollection().AddSingleton(probes).BuildServiceProvider();

        var probed = await ConfigHealth.ProbeAsync(report, services, Config(AllSecretsSet));
        var json = JsonSerializer.Serialize(probed);

        Assert.DoesNotContain("SECRET-google-key", json);
        Assert.DoesNotContain("maps.googleapis.com", json);
        Assert.Equal(ConfigHealth.Error, Find(probed, "probe:geocoding").Status);
    }

    // AC6: a live probe flips its check to the returned status/message; a null (unresolved) probe leaves the
    // static skipped value untouched.
    [Fact]
    public async Task LiveProbe_FlipsStatus()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));
        var probes = new ConfigHealthProbeSet(
            SearXng: _ => Task.FromResult(new ProbeResult(ConfigHealth.Ok, "reachable")),
            Geocoding: _ => Task.FromResult(new ProbeResult(ConfigHealth.Error, "auth failed")),
            ContextScrape: null,
            Hindsight: null);
        var services = new ServiceCollection().AddSingleton(probes).BuildServiceProvider();

        var probed = await ConfigHealth.ProbeAsync(report, services, Config(AllSecretsSet));

        Assert.Equal(ConfigHealth.Ok, Find(probed, "probe:searxng").Status);
        Assert.Equal("reachable", Find(probed, "probe:searxng").Message);
        Assert.Equal(ConfigHealth.Error, Find(probed, "probe:geocoding").Status);
    }

    // AC6 (second half): a probe whose client is unresolved (null delegate) stays skipped — the static value.
    [Fact]
    public async Task UnresolvedProbeClient_IsSkipped()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));
        var probes = new ConfigHealthProbeSet(null, null, null, null);
        var services = new ServiceCollection().AddSingleton(probes).BuildServiceProvider();

        var probed = await ConfigHealth.ProbeAsync(report, services, Config(AllSecretsSet));

        Assert.Equal(ConfigHealth.Skipped, Find(probed, "probe:searxng").Status);
        Assert.Equal(ConfigHealth.Skipped, Find(probed, "probe:geocoding").Status);
        Assert.Equal(ConfigHealth.Skipped, Find(probed, "probe:context.dev").Status);
    }

    // Development renders the prod-infra checks as n-a rather than error/warning noise.
    [Fact]
    public void Development_RendersInfraAsNotApplicable()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env("Development"), Push(enabled: true));

        Assert.Equal(ConfigHealth.NotApplicable, Find(report, "Storage:BlobEndpoint").Status);
        Assert.Equal(ConfigHealth.NotApplicable, Find(report, "AzureOpenAI:TenantId").Status);
    }

    // BC7: the Hindsight key's presence and absence are both reported, and neither reveals the key.
    //
    // The absence assertion is the one that matters. Long-term memory is off by default and not setting the
    // key is the only switch that stops the egress DEPLOYMENT-WIDE (ADR 0056) — so an operator who expects
    // memory and does not have it has exactly one place to find that out, and this is it. Since ADR 0057 it
    // is no longer the only off switch at all: `memory` is a ToolCatalog key each user selects per chat,
    // which is why the PRESENT message is now asserted too — it used to promise that every non-incognito
    // turn was remembered, and that is false for a user who has deselected it.
    [Fact]
    public void ConfigHealth_ReportsHindsight()
    {
        var configured = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));
        var check = Find(configured, "Hindsight:ApiKey");
        Assert.Equal(ConfigHealth.Ok, check.Status);
        Assert.DoesNotContain("SECRET-hindsight-key", JsonSerializer.Serialize(configured));

        // AC12 (ADR 0057): the present message must not claim every turn is recalled and retained — a user
        // who deselected `memory` in Preferences → Tools makes zero Hindsight calls with this key set. It
        // reports the key as CONFIGURED and points at the per-user control instead.
        Assert.Contains("configured", check.Message);
        Assert.Contains("Settings", check.Message);
        Assert.DoesNotContain("every non-incognito turn is recalled and retained", check.Message);

        var without = ConfigHealth.BuildStatic(
            Config(AllSecretsSet.Where(e => e.Key != "Hindsight:ApiKey").ToArray()),
            Env(),
            Push(enabled: true));
        var missing = Find(without, "Hindsight:ApiKey");
        Assert.Equal(ConfigHealth.Warning, missing.Status);
        Assert.Contains("long-term memory is off", missing.Message);

        // Free, unlike the geocoding and context.dev probes — /health is unauthenticated.
        Assert.False(Find(configured, "probe:hindsight").Billable);
        Assert.Equal(ConfigHealth.Skipped, Find(configured, "probe:hindsight").Status);
    }

    // Application Insights export is optional: absent means OTLP only, which is a warning and never an
    // error. The connection string carries the ingestion key, so it must not appear in the report.
    [Fact]
    public void ConfigHealth_ReportsAppInsightsConnectionString()
    {
        const string key = "APPLICATIONINSIGHTS_CONNECTION_STRING";
        var configured = ConfigHealth.BuildStatic(Config(AllSecretsSet), Env(), Push(enabled: true));
        Assert.Equal(ConfigHealth.Ok, Find(configured, key).Status);
        Assert.DoesNotContain("SECRET-appinsights-key", JsonSerializer.Serialize(configured));

        var without = ConfigHealth.BuildStatic(
            Config(AllSecretsSet.Where(e => e.Key != key).ToArray()), Env(), Push(enabled: true));
        var missing = Find(without, key);
        Assert.Equal(ConfigHealth.Warning, missing.Status);
        Assert.Contains("OTLP collector only", missing.Message);
    }

}

/// <summary>A minimal <see cref="IHostEnvironment"/> so <see cref="ConfigHealth.BuildStatic"/> — a pure
/// function — is testable without a host.</summary>
internal sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Agent42.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}
