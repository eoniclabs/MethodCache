using System.Collections.Concurrent;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>
/// A value that asks to be shared for longer than the distributed cache keeps any is shared for that long, and the
/// first such value of each method or manual call name logs a warning that says so.
/// </summary>
public class TagRetentionWarningTests
{
    public interface ILongLived
    {
        [Cache(Duration = "3d", Distributed = true)] Task<int> LongAsync(int id);
        [Cache(Duration = "1h", Distributed = true)] Task<int> ShortAsync(int id);
    }

    public sealed class LongLived : ILongLived
    {
        public Task<int> LongAsync(int id) => Task.FromResult(id);
        public Task<int> ShortAsync(int id) => Task.FromResult(id);
    }

    [Fact]
    public async Task AMethodWhoseDurationOutlivesTheTagRetention_WarnsOnce_AndOneWithinItDoesnt()
    {
        var logs = new Logs();
        await using var provider = Build(logs);
        var service = provider.GetRequiredService<ILongLived>();

        await service.LongAsync(1);
        await service.LongAsync(2);
        await service.ShortAsync(1);

        var warning = Assert.Single(logs.Warnings);
        Assert.Contains(MethodIds.For(typeof(ILongLived), nameof(ILongLived.LongAsync)), warning, StringComparison.Ordinal);
        Assert.Contains("asks to share values for 3.00:00:00", warning, StringComparison.Ordinal);
        Assert.Contains("at most 23:59:58", warning, StringComparison.Ordinal);
        Assert.Contains("Raise TagRetention", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualCalls_WarnOncePerName_AndOnceWithoutOne()
    {
        var logs = new Logs();
        await using var provider = Build(logs);
        var cache = provider.GetRequiredService<ICacheManager>();

        foreach (var key in new[] { "a", "b" })
        {
            await cache.GetOrCreateAsync("report:" + key, _ => ValueTask.FromResult(1),
                o => o.WithDuration(TimeSpan.FromDays(2)).WithDistributed(true).WithName("reports"));
            await cache.SetAsync("plain:" + key, 1, o => o.WithDuration(TimeSpan.FromDays(2)).WithDistributed(true));
        }

        Assert.Equal(2, logs.Warnings.Count);
        Assert.Contains(logs.Warnings, warning => warning.StartsWith("The manual call named 'reports' asks to share values for 2.00:00:00", StringComparison.Ordinal));
        Assert.Contains(logs.Warnings, warning => warning.StartsWith("A manual call without WithName asks to share values for 2.00:00:00", StringComparison.Ordinal));
    }

    private static ServiceProvider Build(Logs logs)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ILongLived, LongLived>();
        services.AddMethodCache(mc => mc.Memory(o => o.MaxExpiration = TimeSpan.FromDays(30)));
        services.AddMethodCacheDistributedCache(_ => new AtomicTestCache(time));
        return services.BuildServiceProvider();
    }

    private sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _messages = new();
        internal IReadOnlyList<string> Warnings => _messages.Where(static m => m.Level == LogLevel.Warning).Select(static m => m.Text).ToArray();
        public ILogger CreateLogger(string categoryName) => new Sink(_messages, categoryName);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<(LogLevel, string)> messages, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                if (category.StartsWith("MethodCache", StringComparison.Ordinal)) messages.Enqueue((level, formatter(state, error)));
            }
        }
    }
}
