using Microsoft.Extensions.Logging;

namespace Assistant.Tests.Fakes;

/// <summary>Records every log entry so a test can assert what was reported.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    /// <summary>Runs on the logging thread for every entry, so a test can react at an exact point (e.g. cancel mid-scan).</summary>
    public Action<LogLevel, string>? OnLog { get; set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_gate) _entries.Add((logLevel, message));
        OnLog?.Invoke(logLevel, message);
    }
}
