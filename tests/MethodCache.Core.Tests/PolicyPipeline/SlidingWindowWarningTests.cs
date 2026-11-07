using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MethodCache.Core.Tests.PolicyPipeline;

/// <summary>
/// A sliding window as long as the duration, or longer, never extends an entry. The build reports it on one attribute
/// (MC0038); the host warns when it starts about every policy, also when the two come from different places.
/// </summary>
public class SlidingWindowWarningTests
{
    [Theory]
    [InlineData(1440)]   // SpiderAds: a day of sliding on a default duration of five minutes
    [InlineData(5)]      // as long as the duration
    public async Task StartHost_SlidingWindowNotShorterThanTheDuration_LogsAWarning(int slidingMinutes)
    {
        var warnings = await StartAsync(mc => mc
            .Defaults(o => o.WithDuration(TimeSpan.FromMinutes(5)))
            .Method((ISlidingReports r) => r.Get(default)).Configure(o => o.WithSlidingExpiration(TimeSpan.FromMinutes(slidingMinutes))));

        var warning = Assert.Single(warnings);
        Assert.Contains("ISlidingReports.Get", warning, StringComparison.Ordinal);
        Assert.Contains("is not shorter than its Duration (00:05:00), so reads never extend its entries", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartHost_SlidingWindowShorterThanTheDuration_LogsNoWarning()
    {
        var warnings = await StartAsync(mc => mc
            .Defaults(o => o.WithDuration(TimeSpan.FromHours(1)))
            .Method((ISlidingReports r) => r.Get(default)).Configure(o => o.WithSlidingExpiration(TimeSpan.FromMinutes(5))));

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task StartHost_DurationFromTheResult_LogsNoWarning()
    {
        // The duration a result gives is only known when it loads
        var warnings = await StartAsync(mc => mc
            .Method((ISlidingReports r) => r.Get(default))
            .DurationFor(value => TimeSpan.FromMinutes(value))
            .Configure(o => o.WithSlidingExpiration(TimeSpan.FromDays(1))));

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task StartHost_SlidingWithoutADuration_LogsNoWarning()
    {
        var warnings = await StartAsync(mc => mc
            .Method((ISlidingReports r) => r.Get(default)).Configure(o => o.WithSlidingExpiration(TimeSpan.FromDays(1))));

        Assert.Empty(warnings);
    }

    private static async Task<string[]> StartAsync(Action<MethodCacheBuilder> configure)
    {
        var logs = new Logs();
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs))
            .ConfigureServices(services => services.AddSingleton<ISlidingReports, SlidingReports>().AddMethodCache(configure))
            .Build();

        await host.StartAsync();
        await host.StopAsync();
        return logs.Warnings.ToArray();
    }

    public interface ISlidingReports
    {
        [Cache]
        int Get(int id);
    }

    public sealed class SlidingReports : ISlidingReports
    {
        public int Get(int id) => id;
    }

    private sealed class Logs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Warnings, categoryName);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> warnings, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                if (level == LogLevel.Warning && category.StartsWith("MethodCache", StringComparison.Ordinal)) warnings.Enqueue(formatter(state, error));
            }
        }
    }
}
