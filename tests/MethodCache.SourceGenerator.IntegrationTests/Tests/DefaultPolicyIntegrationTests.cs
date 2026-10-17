using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// <c>DefaultPolicy</c> and <c>ForGroup</c> reach methods configured only with <c>[Cache]</c>, and values set in
/// the attribute win.
/// </summary>
public class DefaultPolicyIntegrationTests
{
    private const string Source = @"
using System;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace DefaultPolicy
{
    public interface IPrices
    {
        [Cache]
        Task<int> GetDefaultAsync(int id);

        [Cache(Duration = ""00:10:00"")]
        Task<int> GetOwnDurationAsync(int id);

        [Cache(Group = ""short"")]
        Task<int> GetGroupedAsync(int id);
    }

    public class Prices : IPrices
    {
        public static int Calls;
        public Task<int> GetDefaultAsync(int id) => Task.FromResult(++Calls);
        public Task<int> GetOwnDurationAsync(int id) => Task.FromResult(++Calls);
        public Task<int> GetGroupedAsync(int id) => Task.FromResult(++Calls);
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<IPrices, Prices>();
            services.AddMethodCache(config =>
            {
                config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(1)));
                config.Group(""short"", o => o.WithDuration(TimeSpan.FromSeconds(10)));
            });
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Theory]
    [InlineData("GetDefaultAsync", 50, 70)]
    [InlineData("GetOwnDurationAsync", 500, 700)]
    [InlineData("GetGroupedAsync", 5, 15)]
    public async Task Entries_LiveAsLongAsTheMostSpecificSetting(string method, int stillCachedAfterSeconds, int expiredAfterSeconds)
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source, "DefaultPolicyApp");
        var assembly = compiled.Assembly;
        var time = new ManualTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        assembly.GetType("DefaultPolicy.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        var calls = assembly.GetType("DefaultPolicy.Prices")!.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!;
        calls.SetValue(null, 0);

        await using var provider = services.BuildServiceProvider();
        var prices = provider.GetRequiredService(assembly.GetType("DefaultPolicy.IPrices")!);

        Assert.Equal(1, await InvokeAsync(prices, method));
        time.Advance(TimeSpan.FromSeconds(stillCachedAfterSeconds));
        Assert.Equal(1, await InvokeAsync(prices, method));
        time.Advance(TimeSpan.FromSeconds(expiredAfterSeconds - stillCachedAfterSeconds));
        Assert.Equal(2, await InvokeAsync(prices, method));
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
