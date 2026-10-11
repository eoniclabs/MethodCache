using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// <c>SlidingExpiration</c> and <c>RefreshAhead</c> on <c>[Cache]</c> reach the generated decorator and change
/// how long entries live.
/// </summary>
public class ExpirationPolicyIntegrationTests
{
    private const string Source = @"
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace ExpirationPolicy
{
    public interface IPrices
    {
        [Cache(Duration = ""00:10:00"", SlidingExpiration = ""00:01:00"")]
        Task<int> GetSlidingAsync(int id);

        [Cache(Duration = ""00:01:00"", RefreshAhead = ""00:00:30"")]
        Task<int> GetRefreshedAsync(int id);
    }

    public class Prices : IPrices
    {
        public static int Calls;
        public Task<int> GetSlidingAsync(int id) => Task.FromResult(++Calls);
        public Task<int> GetRefreshedAsync(int id) => Task.FromResult(++Calls);
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<IPrices, Prices>();
            services.AddMethodCache();
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task SlidingExpiration_ExtendsEntriesThatAreRead_AndExpiresIdleOnes()
    {
        var (provider, prices, calls, time) = await BuildAsync();
        await using var _ = provider;

        Assert.Equal(1, await InvokeAsync(prices, "GetSlidingAsync"));

        // Each read within the sliding window extends the entry, past the first window
        time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(1, await InvokeAsync(prices, "GetSlidingAsync"));
        time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(1, await InvokeAsync(prices, "GetSlidingAsync"));

        // Left unread for longer than the window, it expires long before its 10 minute duration
        time.Advance(TimeSpan.FromSeconds(70));
        Assert.Equal(2, await InvokeAsync(prices, "GetSlidingAsync"));
        Assert.Equal(2, (int)calls.GetValue(null)!);
    }

    [Fact]
    public async Task RefreshAhead_ReloadsInBackground_WhileServingTheCachedValue()
    {
        var (provider, prices, calls, time) = await BuildAsync();
        await using var _ = provider;

        Assert.Equal(1, await InvokeAsync(prices, "GetRefreshedAsync"));

        // Inside the refresh window the caller still gets the cached value and a reload starts
        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(1, await InvokeAsync(prices, "GetRefreshedAsync"));

        // The reload bumps Calls before its value is stored, so wait for the cache to serve the new value
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (await InvokeAsync(prices, "GetRefreshedAsync") != 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The entry was not refreshed in the background.");
            await Task.Delay(10);
        }

        Assert.Equal(2, (int)calls.GetValue(null)!);
    }

    private async Task<(ServiceProvider Provider, object Prices, FieldInfo Calls, ManualTimeProvider Time)> BuildAsync()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source, "ExpirationPolicyApp");
        var assembly = compiled.Assembly;
        var interfaceType = assembly.GetType("ExpirationPolicy.IPrices")!;
        var time = new ManualTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        assembly.GetType("ExpirationPolicy.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        var calls = assembly.GetType("ExpirationPolicy.Prices")!.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!;
        calls.SetValue(null, 0);
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService(interfaceType), calls, time);
    }

    private static Task<int> InvokeAsync(object service, string method)
        => (Task<int>)InterfaceMethods.Find(service, method).Invoke(service, new object[] { 1 })!;

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
