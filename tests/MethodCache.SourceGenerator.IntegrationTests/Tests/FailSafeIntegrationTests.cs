using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Fail-safe set centrally (<c>config.FailSafe</c> or the <c>MethodCache:FailSafe</c> section) reaches every cached
/// method, and <c>[Cache(FailSafe = ...)]</c> changes it for one method.
/// </summary>
public class FailSafeIntegrationTests
{
    private const string Source = @"
using System;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace FailSafe
{
    public interface IPrices
    {
        [Cache(Duration = ""00:01:00"")]
        Task<int> GetCentralAsync(int id);

        [Cache(Duration = ""00:01:00"", FailSafe = ""off"")]
        Task<int> GetBalanceAsync(int id);
    }

    public interface IRates
    {
        [Cache(Duration = ""00:01:00"", FailSafe = ""01:00:00"")]
        Task<int> GetRateAsync(int id);
    }

    public class Prices : IPrices, IRates
    {
        public static int Calls;
        public static bool Down;

        public Task<int> GetCentralAsync(int id) => Load();
        public Task<int> GetBalanceAsync(int id) => Load();
        public Task<int> GetRateAsync(int id) => Load();

        private static Task<int> Load()
        {
            if (Down) throw new InvalidOperationException(""database is down"");
            return Task.FromResult(++Calls);
        }
    }

    public static class Startup
    {
        public static void ConfigureCentral(IServiceCollection services)
        {
            services.AddSingleton<IPrices, Prices>();
            services.AddMethodCache(config => config.Defaults(o => o.FailSafe(TimeSpan.FromHours(1))));
        }

        public static void ConfigureForSettings(IServiceCollection services)
        {
            services.AddSingleton<IPrices, Prices>();
            services.AddMethodCache();
        }

        public static void ConfigureAttributeOnly(IServiceCollection services)
        {
            services.AddSingleton<IRates, Prices>();
            services.AddMethodCache();
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Theory]
    [InlineData("central")]
    [InlineData("settings")]
    public async Task CentralFailSafe_ServesTheOldValue_UnlessTheMethodTurnsItOff(string configuredBy)
    {
        var (assembly, time, provider) = await CreateAsync(configuredBy);
        await using var _ = provider;
        var prices = provider.GetRequiredService(assembly.GetType("FailSafe.IPrices")!);

        Assert.Equal(1, await InvokeAsync(prices, "GetCentralAsync"));
        Assert.Equal(2, await InvokeAsync(prices, "GetBalanceAsync"));

        time.Advance(TimeSpan.FromMinutes(2));
        SetDown(assembly, true);

        Assert.Equal(1, await InvokeAsync(prices, "GetCentralAsync"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(prices, "GetBalanceAsync"));
    }

    [Fact]
    public async Task AttributeFailSafe_WorksWithoutCentralSetting()
    {
        var (assembly, time, provider) = await CreateAsync("attribute");
        await using var _ = provider;
        var rates = provider.GetRequiredService(assembly.GetType("FailSafe.IRates")!);

        Assert.Equal(1, await InvokeAsync(rates, "GetRateAsync"));
        time.Advance(TimeSpan.FromMinutes(2));
        SetDown(assembly, true);

        Assert.Equal(1, await InvokeAsync(rates, "GetRateAsync"));
    }

    private async Task<(Assembly Assembly, ManualTimeProvider Time, ServiceProvider Provider)> CreateAsync(string configuredBy)
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source, "FailSafeApp");
        var assembly = compiled.Assembly;
        var time = new ManualTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        var startup = assembly.GetType("FailSafe.Startup")!;
        switch (configuredBy)
        {
            case "central":
                startup.GetMethod("ConfigureCentral")!.Invoke(null, new object[] { services });
                break;
            case "settings":
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["MethodCache:FailSafe:Duration"] = "01:00:00" })
                    .Build();
                startup.GetMethod("ConfigureForSettings")!.Invoke(null, new object[] { services });
                services.AddMethodCacheFromConfiguration(configuration);
                break;
            default:
                startup.GetMethod("ConfigureAttributeOnly")!.Invoke(null, new object[] { services });
                break;
        }

        var prices = assembly.GetType("FailSafe.Prices")!;
        prices.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, 0);
        SetDown(assembly, false);

        return (assembly, time, services.BuildServiceProvider());
    }

    private static void SetDown(Assembly assembly, bool down)
        => assembly.GetType("FailSafe.Prices")!.GetField("Down", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, down);

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
