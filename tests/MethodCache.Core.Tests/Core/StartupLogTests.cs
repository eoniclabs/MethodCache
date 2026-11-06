using MethodCache.Core.Tests.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// When the cache starts it logs which layers it uses, so configuration that drifted apart between instances, such as a
/// missing distributed cache or another key prefix, shows in the log.
/// </summary>
public sealed class StartupLogTests
{
    [Fact]
    public void WithoutADistributedCache_SaysEachInstanceCachesOnItsOwn()
    {
        var logs = new LogCapture();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs));
        services.AddMethodCache(mc => mc.Memory(o => o.MaxItems = 500));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheManager>();

        var line = Assert.Single(logs.Information);
        Assert.Contains("up to 500 entries per instance, without a distributed cache", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WithADistributedCacheAndBackplane_NamesTheStoreKeyPrefixAndBackplane()
    {
        var logs = new LogCapture();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs));
        services.AddKeyedSingleton<MethodCache.Extensibility.IBackplane>(MethodCache.Infrastructure.OwnedServices.Key, new ChaosBackplaneHub().Join());
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache(), o => o.KeyPrefix = "billing:");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheManager>();

        var line = Assert.Single(logs.Information);
        Assert.Contains("in the distributed cache AtomicTestCache under the key prefix 'billing:', with the Member backplane", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WithADistributedCacheWithoutBackplane_SaysOtherInstancesKeepTheirCopy()
    {
        var logs = new LogCapture();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs));
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheManager>();

        var line = Assert.Single(logs.Information);
        Assert.Contains("under the key prefix 'methodcache:', without a backplane", line, StringComparison.Ordinal);
    }

    private sealed class LogCapture : ILoggerProvider, ILogger
    {
        public List<string> Information { get; } = [];

        public ILogger CreateLogger(string categoryName) => categoryName.EndsWith("InMemoryCacheManager", StringComparison.Ordinal) ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) lock (Information) Information.Add(formatter(state, exception));
        }

        public void Dispose() { }
    }
}
