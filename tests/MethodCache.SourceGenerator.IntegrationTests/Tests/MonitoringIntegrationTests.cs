using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MethodCache;
using MethodCache.Extensibility;
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
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
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

        var services = new ServiceCollection();
        assembly.GetType("Monitoring.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        await using var provider = services.BuildServiceProvider();
        using var metrics = new CountingMetrics(provider.GetRequiredService<IMeterFactory>());
        var interfaceType = assembly.GetType("Monitoring.IUserService")!;
        var service = provider.GetRequiredService(interfaceType);
        var getUser = interfaceType.GetMethod("GetUserAsync")!;

        await (Task<string>)getUser.Invoke(service, new object[] { 42 })!; // miss
        await (Task<string>)getUser.Invoke(service, new object[] { 42 })!; // hit on the decorator's fast path
        await (Task<string>)getUser.Invoke(service, new object[] { 7 })!;  // miss

        Assert.Equal(1, metrics.Hits);
        Assert.Equal(2, metrics.Misses);
        Assert.Equal(2, metrics.Latencies.Count);
        Assert.All(metrics.Latencies, elapsed => Assert.True(elapsed >= TimeSpan.Zero));

        var stats = await provider.GetRequiredService<ICacheAdministrator>().GetStatsAsync();
        Assert.Equal(2, stats.LocalMethodEntries);
    }

    [Fact]
    public async Task MethodInheritedFromAGenericBase_UsesItsDeclaredPolicyMethodId()
    {
        const string source = @"
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;

namespace Monitoring
{
    public sealed record User(int Id);
    public sealed record Order(int Id);
    public interface IRepository<T> { [Cache] Task<string> GetAsync(int id); }
    public interface IUserRepo : IRepository<User> { }
    public interface IOrderRepo : IRepository<Order> { }
    public class UserRepo : IUserRepo { public Task<string> GetAsync(int id) => Task.FromResult(""u""); }
    public class OrderRepo : IOrderRepo { public Task<string> GetAsync(int id) => Task.FromResult(""o""); }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<IUserRepo, UserRepo>();
            services.AddSingleton<IOrderRepo, OrderRepo>();
            services.AddMethodCache();
        }
    }
}";
        var assembly = (await _engine.CompileWithSourceGeneratorAsync(source, "MonitoringGenericBaseNames")).Assembly;

        var services = new ServiceCollection();
        assembly.GetType("Monitoring.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        await using var provider = services.BuildServiceProvider();
        using var metrics = new CountingMetrics(provider.GetRequiredService<IMeterFactory>());

        foreach (var (name, entity) in new[] { ("Monitoring.IUserRepo", "Monitoring.User"), ("Monitoring.IOrderRepo", "Monitoring.Order") })
        {
            var service = provider.GetRequiredService(assembly.GetType(name)!);
            var get = assembly.GetType("Monitoring.IRepository`1")!.MakeGenericType(assembly.GetType(entity)!).GetMethod("GetAsync")!;
            await (Task<string>)get.Invoke(service, new object[] { 1 })!; // miss
            await (Task<string>)get.Invoke(service, new object[] { 1 })!; // hit
        }

        Assert.Equal(new[] { "Monitoring.IRepository<T>.GetAsync" }, metrics.Names.Distinct().Order());
    }

    [Fact]
    public async Task SameMethodNameOnTwoInterfaces_IsReportedAsTwoMethods_ByMethodId()
    {
        const string source = @"
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;

namespace Monitoring
{
    public interface IUserService { [Cache] Task<string> GetAsync(int id); }
    public interface IOrderService { [Cache] Task<string> GetAsync(int id); }
    public class UserService : IUserService { public Task<string> GetAsync(int id) => Task.FromResult(""u""); }
    public class OrderService : IOrderService { public Task<string> GetAsync(int id) => Task.FromResult(""o""); }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<IUserService, UserService>();
            services.AddSingleton<IOrderService, OrderService>();
            services.AddMethodCache();
        }
    }
}";
        var assembly = (await _engine.CompileWithSourceGeneratorAsync(source, "MonitoringMethodNames")).Assembly;

        var services = new ServiceCollection();
        assembly.GetType("Monitoring.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        await using var provider = services.BuildServiceProvider();
        using var metrics = new CountingMetrics(provider.GetRequiredService<IMeterFactory>());

        foreach (var name in new[] { "Monitoring.IUserService", "Monitoring.IOrderService" })
        {
            var interfaceType = assembly.GetType(name)!;
            var service = provider.GetRequiredService(interfaceType);
            var get = interfaceType.GetMethod("GetAsync")!;
            await (Task<string>)get.Invoke(service, new object[] { 1 })!; // miss
            await (Task<string>)get.Invoke(service, new object[] { 1 })!; // hit
        }

        // The id the admin endpoints and appsettings.json use, so dashboards and policies name the same method
        Assert.Equal(new[] { "Monitoring.IOrderService.GetAsync", "Monitoring.IUserService.GetAsync" }, metrics.Names.Distinct().Order());
    }

    private sealed class CountingMetrics : IDisposable
    {
        private readonly MeterListener _listener;
        private int _hits;
        private int _misses;
        public int Hits => _hits;
        public int Misses => _misses;
        public ConcurrentBag<TimeSpan> Latencies { get; } = new();
        public ConcurrentBag<string> Names { get; } = new();
        internal CountingMetrics(IMeterFactory factory)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter.Scope, factory)) listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                if (instrument.Name != "methodcache.requests") return;
                foreach (var tag in tags)
                {
                    if (tag.Key == "name") Names.Add((string)tag.Value!);
                    if (tag.Key == "result" && Equals(tag.Value, "hit")) Interlocked.Increment(ref _hits);
                    if (tag.Key == "result" && Equals(tag.Value, "miss")) Interlocked.Increment(ref _misses);
                }
            });
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (instrument.Name == "methodcache.load.duration") Latencies.Add(TimeSpan.FromSeconds(value));
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
