using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// docs/user-guide/DIAGNOSTICS.md#mcg019 and docs/troubleshooting.md: a cached method whose result type is
/// <c>object</c> or an interface keeps its results in each instance's memory when a distributed cache is used, so
/// another instance loads its own instead of getting a <c>JsonElement</c> or a deserialization error.
/// </summary>
public class UnrestorableResultTypeTests
{
    public interface IShape
    {
        double Area { get; }
    }

    public sealed class Square : IShape
    {
        public double Side { get; set; }

        public double Area => Side * Side;
    }

    public sealed class Report
    {
        public int Id { get; set; }

        public string Title { get; set; } = "";
    }

#pragma warning disable MCG019 // what this test is about: these results can't be shared through the distributed cache
    public interface IReports
    {
        [Cache] Task<object> GetAsync(int id);

        [Cache] Task<IShape> ShapeAsync(int id);

        [Cache] Task<Report> ReportAsync(int id);
    }
#pragma warning restore MCG019

    private sealed class Reports : IReports
    {
        public int Calls;

        public Task<object> GetAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<object>(new Report { Id = id, Title = "report " + id });
        }

        public Task<IShape> ShapeAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IShape>(new Square { Side = id });
        }

        public Task<Report> ReportAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new Report { Id = id, Title = "report " + id });
        }
    }

    private readonly MethodCache.TestSupport.AtomicTestCache _shared = new();
    private readonly FakeTimeProvider _time = new();

    private (ServiceProvider Provider, Reports Reports, WarningLog Log) Instance()
    {
        var reports = new Reports();
        var log = new WarningLog();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddLogging(logging => logging.AddProvider(log));
        services.AddSingleton<IReports>(reports);
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => _shared);
        return (services.BuildServiceProvider(), reports, log);
    }

    [Fact]
    public async Task ObjectAndInterfaceResults_StayInEachInstancesMemory_WithOneWarningPerMethod()
    {
        var (firstProvider, first, firstLog) = Instance();
        var (secondProvider, second, _) = Instance();
        using (firstProvider)
        using (secondProvider)
        {
            var a = firstProvider.GetRequiredService<IReports>();
            await a.GetAsync(1);
            await a.GetAsync(2);
            await a.ShapeAsync(3);
            await a.ShapeAsync(4);
            Assert.Equal(4, first.Calls);

            // The first instance still serves them from its own memory
            Assert.Equal("report 1", Assert.IsType<Report>(await a.GetAsync(1)).Title);
            Assert.Equal(4, first.Calls);

            // The second instance gets no JsonElement and no deserialization error: it loads its own
            var b = secondProvider.GetRequiredService<IReports>();
            Assert.Equal("report 1", Assert.IsType<Report>(await b.GetAsync(1)).Title);
            Assert.Equal(9, Assert.IsType<Square>(await b.ShapeAsync(3)).Area);
            Assert.Equal(2, second.Calls);

            // A concrete result type is shared through the same distributed cache
            await a.ReportAsync(5);
            Assert.Equal("report 5", (await b.ReportAsync(5)).Title);
            Assert.Equal(2, second.Calls);

            // One warning per method, not one per write
            Assert.Single(firstLog.Warnings, w => w.Contains("GetAsync", StringComparison.Ordinal) && w.Contains("in memory only", StringComparison.Ordinal));
            Assert.Single(firstLog.Warnings, w => w.Contains("ShapeAsync", StringComparison.Ordinal) && w.Contains("in memory only", StringComparison.Ordinal));
        }
    }

    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
