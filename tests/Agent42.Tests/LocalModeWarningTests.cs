using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>AC15: local storage logs the no-authentication warning once; Azure storage never does.</summary>
public sealed class LocalModeWarningTests
{
    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IConfiguration Config(string? provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = provider })
            .Build();

    [Fact]
    public void Local_LogsTheWarning_AtWarningLevel()
    {
        var logger = new CollectingLogger();

        Assert.True(LocalModeWarning.LogIfLocal(Config("local"), logger));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("local-dev", entry.Message);
        Assert.Contains("no authentication", entry.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("azure")]
    public void Azure_LogsNothing(string? provider)
    {
        var logger = new CollectingLogger();

        Assert.False(LocalModeWarning.LogIfLocal(Config(provider), logger));

        Assert.Empty(logger.Entries);
    }
}
