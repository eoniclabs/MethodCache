using System.Collections.Concurrent;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Verifies what monitoring sees when a generated decorator runs on the default in-memory cache manager.
/// </summary>
public class MonitoringIntegrationTests
{
    private const string Source = @"
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Monitoring
{
    public interface IUserService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetUserAsync(int userId);
    }

    public class UserService : IUserService
    {
        public async Task<string> GetUserAsync(int userId)
        {
            await Task.Delay(5);
            return ""user-"" + userId;
        }
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<IUserService, UserService>();
            services.AddMethodCache();
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task CachedCalls_AreReportedOnce_WithLoadLatency_AndStatisticsInDI()
    {
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(Source, "MonitoringApp");
        var assembly = testAssembly.Assembly;

        var metrics = new CountingMetricsProvider();
        var services = new ServiceCollection();
        services.AddSingleton<ICacheMetricsProvider>(metrics);
        assembly.GetType("Monitoring.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        await using var provider = services.BuildServiceProvider();
        var interfaceType = assembly.GetType("Monitoring.IUserService")!;
        var service = provider.GetRequiredService(interfaceType);
        var getUser = interfaceType.GetMethod("GetUserAsync")!;

        await (Task<string>)getUser.Invoke(service, new object[] { 42 })!; // miss
        await (Task<string>)getUser.Invoke(service, new object[] { 42 })!; // hit on the decorator's fast path
        await (Task<string>)getUser.Invoke(service, new object[] { 7 })!;  // miss

        Assert.Equal(1, metrics.Hits);
        Assert.Equal(2, metrics.Misses);
        Assert.Equal(2, metrics.Latencies.Count);
        Assert.All(metrics.Latencies, elapsed => Assert.True(elapsed >= 0));

        var stats = await provider.GetRequiredService<IMemoryCache>().GetStatsAsync();
        Assert.Equal(2, stats.Entries);
    }

    private sealed class CountingMetricsProvider : ICacheMetricsProvider
    {
        private int _hits;
        private int _misses;

        public int Hits => _hits;

        public int Misses => _misses;

        public ConcurrentBag<long> Latencies { get; } = new();

        public void CacheHit(string methodName) => Interlocked.Increment(ref _hits);

        public void CacheMiss(string methodName) => Interlocked.Increment(ref _misses);

        public void CacheError(string methodName, string errorMessage)
        {
        }

        public void CacheLatency(string methodName, long elapsedMilliseconds) => Latencies.Add(elapsedMilliseconds);
    }
}
