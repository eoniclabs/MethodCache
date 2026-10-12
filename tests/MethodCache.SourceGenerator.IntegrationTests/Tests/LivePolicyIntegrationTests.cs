using System;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache;
using MethodCache.Policies;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Generated decorators read their policy through a shared handle, so runtime overrides reach services that
/// already exist, and scoped decorators do not resolve policies again on every resolve.
/// </summary>
public class LivePolicyIntegrationTests
{
    private const string Source = @"
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace LivePolicy
{
    public interface IPrices
    {
        [Cache(Duration = ""00:10:00"")]
        Task<int> GetAsync(int id);
    }

    public class Prices : IPrices
    {
        public static int Calls;
        public Task<int> GetAsync(int id) => Task.FromResult(++Calls);
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddScoped<IPrices, Prices>();
            services.AddMethodCache();
        }
    }
}";

    private const string MethodId = "LivePolicy.IPrices.GetAsync";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task RuntimeVersionOverride_ReachesAnExistingDecorator()
    {
        var (provider, interfaceType, calls) = await BuildAsync();
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var prices = scope.ServiceProvider.GetRequiredService(interfaceType);

        Assert.Equal(1, await GetAsync(prices, 1));
        Assert.Equal(1, await GetAsync(prices, 1));

        // A new version means new keys, so the next call on the same decorator loads again
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        await configurator.UpsertAsync(MethodId, b => b.WithVersion(2));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((int)calls.GetValue(null)! == 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "The override did not reach the decorator.");
            await GetAsync(prices, 1);
            await Task.Delay(10);
        }

        Assert.Equal(2, await GetAsync(prices, 1));
        Assert.Equal(2, (int)calls.GetValue(null)!);
    }

    [Fact]
    public async Task ScopedDecorators_ShareTheirCachedMethods()
    {
        var (provider, interfaceType, _) = await BuildAsync();
        await using var _ = provider;
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();

        var first = firstScope.ServiceProvider.GetRequiredService(interfaceType);
        var second = secondScope.ServiceProvider.GetRequiredService(interfaceType);
        Assert.NotSame(first, second);

        var field = first.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.Name.StartsWith("_method_", StringComparison.Ordinal));
        Assert.Same(field.GetValue(first), field.GetValue(second));
    }

    private async Task<(ServiceProvider Provider, Type InterfaceType, FieldInfo Calls)> BuildAsync()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source, "LivePolicyApp");
        var assembly = compiled.Assembly;

        var services = new ServiceCollection();
        assembly.GetType("LivePolicy.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        var calls = assembly.GetType("LivePolicy.Prices")!.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!;
        calls.SetValue(null, 0);
        return (services.BuildServiceProvider(), assembly.GetType("LivePolicy.IPrices")!, calls);
    }

    private static Task<int> GetAsync(object service, int id)
        => (Task<int>)InterfaceMethods.Find(service, "GetAsync").Invoke(service, new object[] { id })!;
}
