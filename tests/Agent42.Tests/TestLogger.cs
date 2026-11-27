using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>One captured log call: level, formatted message and the exception it carried.</summary>
public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// The shared log-capture logger for tests (cc-03): records every entry, never filters by level. It is both an
/// <see cref="ILogger"/> and an <see cref="ILogger{T}"/>, so it fits either constructor shape.
/// </summary>
public sealed class TestLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public IReadOnlyList<string> Messages => [.. _entries.Select(e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
}

/// <summary>The untyped form of <see cref="TestLogger{T}"/>, for code that takes a plain <see cref="ILogger"/>.</summary>
public sealed class TestLogger : ILogger
{
    private readonly TestLogger<object> _inner = new();

    public IReadOnlyList<LogEntry> Entries => _inner.Entries;

    public IReadOnlyList<string> Messages => _inner.Messages;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
