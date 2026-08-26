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
        ("GoogleCalendar:ClientId", "SECRET-google-oauth-client-id"),
        ("GoogleCalendar:ClientSecret", "SECRET-google-oauth-client-secret"),
    ];

    private static ConfigCheck Find(ConfigHealthReport report, string key) =>
        report.Checks.Single(c => c.Key == key);

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
        Assert.Equal(ConfigHealth.Ok, Find(report, "GoogleCalendar:ClientId").Status);
        Assert.Equal(ConfigHealth.Ok, Find(report, "GoogleCalendar:ClientSecret").Status);
    }

    // AC11 (ADR 0053). Presence only, and the two halves are reported SEPARATELY: a Google Web
    // application client is confidential, so setting only the id leaves the token exchange broken, and
    // an operator who did that has to be able to see which half is missing.
    [Fact]
    public void ConfigHealth_ReportsGoogleCalendarClientPresence()
    {
        var report = ConfigHealth.BuildStatic(
            Config(AllSecretsSet.Where(e => e.Key != "GoogleCalendar:ClientSecret").ToArray()),
            Env(), Push(enabled: true));

        Assert.Equal(ConfigHealth.Ok, Find(report, "GoogleCalendar:ClientId").Status);
        Assert.Equal(ConfigHealth.Warning, Find(report, "GoogleCalendar:ClientSecret").Status);

        // Neither check carries a VALUE — the whole report is a status plus a curated message, and a
        // secret leaking into the Configuration tab would be visible to every allowlisted user.
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("SECRET-google-oauth-client-id", json, StringComparison.Ordinal);

        // There is deliberately NO live probe (derived decision (k)): every other probe here is
        // operator-keyed, but a calendar probe would need a USER's OAuth token and would be meaningless
        // with none connected. Asserted against the shape probe keys ACTUALLY take — every one of them is
        // "probe:*" and carries Billable — because an assertion phrased against a display name could
        // never fail, and this rule is otherwise unguarded.
        Assert.DoesNotContain(
            report.Checks,
            c => c.Key.StartsWith("probe:", StringComparison.Ordinal)
                && c.Key.Contains("google", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, c => c.Billable && c.Key.Contains("Calendar", StringComparison.Ordinal));
    }

    // AC1: a missing optional secret is a warning, not an error (the app boots fine without it).
    [Fact]
    public void MissingOptionalSecret_IsWarning()
    {
        var report = ConfigHealth.BuildStatic(Config(AllSecretsSet.Where(e => e.Key != "Geocoding:GoogleApiKey").ToArray()),
            Env(), Push(enabled: true));

        Assert.Equal(ConfigHealth.Warning, Find(report, "Geocoding:GoogleApiKey").Status);
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
            ContextScrape: null);
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
            ContextScrape: null);
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
        var probes = new ConfigHealthProbeSet(null, null, null);
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
