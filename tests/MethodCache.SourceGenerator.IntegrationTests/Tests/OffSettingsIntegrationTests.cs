using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Policies;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// <c>"off"</c> turns an inherited sliding expiration or refresh-ahead off for one method, as it does for fail-safe.
/// </summary>
public class OffSettingsIntegrationTests
{
    private const string Source = @"
using System;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;

namespace Off
{
    public interface IPrices
    {
        [Cache(Duration = ""00:10:00"")]
        Task<int> GetCentralAsync(int id);

        [Cache(Duration = ""00:10:00"", SlidingExpiration = ""off"", RefreshAhead = ""off"")]
        Task<int> GetPinnedAsync(int id);
    }

    public class Prices : IPrices
    {
        public static int Calls;

        public Task<int> GetCentralAsync(int id) => Task.FromResult(++Calls);
        public Task<int> GetPinnedAsync(int id) => Task.FromResult(++Calls);
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<IPrices, Prices>();
            services.AddMethodCache(config => config.Defaults(o => o
                .WithSlidingExpiration(TimeSpan.FromMinutes(2))
                .WithRefreshAhead(TimeSpan.FromSeconds(30))));
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task Off_TurnsInheritedSlidingAndRefreshAheadOff_ForOneMethod()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source, "OffApp");
        var assembly = compiled.Assembly;
        var time = new ManualTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        assembly.GetType("Off.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        assembly.GetType("Off.Prices")!.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, 0);
        await using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IPolicyRegistry>();
        var central = registry.GetPolicy("[OffApp]Off.IPrices.GetCentralAsync").Policy;
        var pinned = registry.GetPolicy("[OffApp]Off.IPrices.GetPinnedAsync").Policy;
        Assert.Equal(TimeSpan.FromMinutes(2), central.SlidingExpiration);
        Assert.Equal(TimeSpan.FromSeconds(30), central.RefreshAhead);
        Assert.Equal(TimeSpan.Zero, pinned.SlidingExpiration);
        Assert.Equal(TimeSpan.Zero, pinned.RefreshAhead);

        var prices = provider.GetRequiredService(assembly.GetType("Off.IPrices")!);
        Assert.Equal(1, await InvokeAsync(prices, "GetCentralAsync"));
        Assert.Equal(2, await InvokeAsync(prices, "GetPinnedAsync"));

        // Unused for longer than the sliding window: the central method reloads, the pinned one lives its duration
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(3, await InvokeAsync(prices, "GetCentralAsync"));
        Assert.Equal(2, await InvokeAsync(prices, "GetPinnedAsync"));
    }

    private static Task<int> InvokeAsync(object service, string method)
    {
        foreach (var contract in service.GetType().GetInterfaces())
        {
            if (contract.GetMethod(method) is { } target)
            {
                return (Task<int>)target.Invoke(service, new object[] { 1 })!;
            }
        }

        throw new MissingMethodException(service.GetType().Name, method);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
