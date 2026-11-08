using System.Collections.Concurrent;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// docs/features/distributed-cache.md#results-that-dont-change: in Development, MethodCache writes the values it keeps
/// in memory again now and then, and names the method of one that code changed after it was cached, since the memory
/// cache hands that same instance to later callers while other instances read the stored copy.
/// </summary>
public class ChangedResultTests
{
    public sealed class Order
    {
        public int Id { get; set; }

        public string Status { get; set; } = "open";
    }

    public interface IOrders
    {
        [Cache] Task<List<Order>> ForCustomerAsync(int customerId);
    }

    private sealed class Orders : IOrders
    {
        public Task<List<Order>> ForCustomerAsync(int customerId) => Task.FromResult(new List<Order> { new() { Id = 1 }, new() { Id = 2 } });
    }

    private readonly FakeTimeProvider _time = new();

    private (ServiceProvider Provider, WarningLog Log) Instance(string environment)
    {
        var log = new WarningLog();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IHostEnvironment>(new Environment(environment));
        services.AddLogging(logging => logging.AddProvider(log));
        services.AddSingleton<IOrders, Orders>();
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache(_time));
        return (services.BuildServiceProvider(), log);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("set")]
    public async Task InDevelopment_AResultChangedAfterItWasCached_IsNamedInAWarning(string change)
    {
        var (provider, log) = Instance(Environments.Development);
        using (provider)
        {
            var orders = await provider.GetRequiredService<IOrders>().ForCustomerAsync(7);
            if (change == "add") orders.Add(new Order { Id = 3 });
            else orders[0].Status = "shown";

            _time.Advance(TimeSpan.FromSeconds(2));

            var warning = Assert.Single(log.Warnings);
            Assert.StartsWith("A cached result of ", warning, StringComparison.Ordinal);
            Assert.Contains("ForCustomerAsync", warning, StringComparison.Ordinal);
            Assert.Contains("was changed after it was cached", warning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InDevelopment_AnUnchangedResult_GetsNoWarning()
    {
        var (provider, log) = Instance(Environments.Development);
        using (provider)
        {
            var orders = await provider.GetRequiredService<IOrders>().ForCustomerAsync(7);
            Assert.Equal(2, orders.Count);
            Assert.NotNull(provider.GetRequiredService<InMemoryCacheManager>().ChangeCheck);

            _time.Advance(TimeSpan.FromSeconds(2));

            Assert.Empty(log.Warnings);
        }
    }

    [Fact]
    public async Task OutsideDevelopment_NothingIsChecked()
    {
        var (provider, log) = Instance(Environments.Production);
        using (provider)
        {
            var orders = await provider.GetRequiredService<IOrders>().ForCustomerAsync(7);
            orders.Add(new Order { Id = 3 });

            _time.Advance(TimeSpan.FromSeconds(2));

            Assert.Null(provider.GetRequiredService<InMemoryCacheManager>().ChangeCheck);
            Assert.Empty(log.Warnings);
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Docs.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Enqueue(formatter(state, exception));
        }

        public void Dispose()
        {
        }
    }
}
