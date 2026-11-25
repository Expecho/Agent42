using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>ADR 0145: the entra-mode silent failures (ephemeral Data Protection ring, a <c>local-dev</c> owner) are loud.</summary>
public sealed class AuthConfigHealthTests
{
    private const string Oid = "11111111-1111-1111-1111-111111111111";

    private static IConfiguration Config(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static ConfigHealthReport Report(IConfiguration config)
        => ConfigHealth.BuildStatic(config, new FakeHostEnvironment("Production"), new PushConfig(false, "", "mailto:x@y"));

    private static ConfigCheck Find(ConfigHealthReport report, string key) => report.Checks.Single(c => c.Key == key);

    [Fact]
    public void ConfigHealth_EasyAuth_ReportsModeOnly()
    {
        var report = Report(Config());

        Assert.Equal(ConfigHealth.Ok, Find(report, "Auth:Mode").Status);
        Assert.DoesNotContain(report.Checks, c => c.Key.StartsWith("Auth:DataProtection", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigHealth_Entra_AzureWithConnectionString_FlagsEphemeralRing()
    {
        var report = Report(Config(("Auth:Mode", "entra"), ("ConnectionStrings:blobs", "UseDevelopmentStorage=true")));

        Assert.Equal(ConfigHealth.Error, Find(report, "Auth:DataProtectionKeyRing").Status);
    }

    [Fact]
    public void ConfigHealth_Entra_AzureWithBlobEndpoint_RingPersisted()
    {
        var report = Report(Config(("Auth:Mode", "entra"), ("Storage:BlobEndpoint", "https://acct.blob.core.windows.net")));

        Assert.Equal(ConfigHealth.Ok, Find(report, "Auth:DataProtectionKeyRing").Status);
    }

    [Fact]
    public void ConfigHealth_Entra_LocalStorage_RingPersistedOnTheVolume()
    {
        var report = Report(Config(("Auth:Mode", "entra"), ("Storage:Provider", "local")));

        Assert.Equal(ConfigHealth.Ok, Find(report, "Auth:DataProtectionKeyRing").Status);
    }

    [Fact]
    public void ConfigHealth_Entra_LocalDevOwner_IsRed()
    {
        var bad = Report(Config(("Auth:Mode", "entra"), ("Access:Owners:0", "local-dev")));
        var good = Report(Config(("Auth:Mode", "entra"), ("Access:Owners:0", Oid)));

        Assert.Equal(ConfigHealth.Error, Find(bad, "Auth:OwnersLocalDev").Status);
        Assert.Equal(ConfigHealth.Ok, Find(good, "Auth:OwnersLocalDev").Status);
    }

    [Fact]
    public void StartupWarnings_Entra_LogEachFailure_AndEasyAuthLogsNothing()
    {
        var logger = new ListLogger();

        var easy = AuthStartupChecks.LogIfEntra(Config(("Access:Owners:0", "local-dev")), logger);
        var entra = AuthStartupChecks.LogIfEntra(
            Config(("Auth:Mode", "entra"), ("ConnectionStrings:blobs", "x"), ("Access:Owners:0", "local-dev")), logger);

        Assert.Equal(0, easy);
        Assert.Equal(2, entra);
        Assert.Equal([AuthStartupChecks.DataProtectionWarning, AuthStartupChecks.LocalDevOwnerWarning], logger.Warnings);
    }

    [Fact]
    public void LocalModeWarning_IsSilent_WhenEntraProvidesSignIn()
    {
        var logger = new ListLogger();

        var local = LocalModeWarning.LogIfLocal(Config(("Storage:Provider", "local")), logger);
        var localEntra = LocalModeWarning.LogIfLocal(Config(("Storage:Provider", "local"), ("Auth:Mode", "entra")), logger);

        Assert.True(local);
        Assert.False(localEntra);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
