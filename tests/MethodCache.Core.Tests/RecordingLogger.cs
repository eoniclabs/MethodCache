using Microsoft.Extensions.Logging;

namespace MethodCache.Core.Tests;

/// <summary>Keeps every message logged, thread-safely.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _messages = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Messages
    {
        get { lock (_messages) return _messages.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    {
        lock (_messages) _messages.Add((level, formatter(state, error)));
    }
}
