using System.Reflection;
using MethodCache.Core.Management;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// [assembly: CacheInterface] caches an interface from another assembly (here this test assembly, which the
/// compiled sources reference like a NuGet package) without a wrapper.
/// </summary>
public class CacheInterfaceIntegrationTests
{
    private const string Source = @"
using System;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;
using Microsoft.Extensions.DependencyInjection;

[assembly: CacheInterface(typeof(IFxVendorClient),
    nameof(IFxVendorClient.GetRateAsync), nameof(IFxVendorClient.GetCurrenciesAsync), nameof(IFxVendorClient.Square),
    Duration = ""00:01:00"", Tags = new[] { ""fx"", ""fx:{from}"" })]

namespace VendorApp
{
    public static class Startup
    {
        public static void Configure(IServiceCollection services, FxVendorClient client)
        {
            services.AddSingleton<IFxVendorClient>(client);
            services.AddMethodCache(config => config
                .ForService<IFxVendorClient>()
                .Method(c => c.GetCurrenciesAsync())
                .Configure(o => o.WithDuration(TimeSpan.FromHours(1))));
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task ListedMethods_AreCached_AndOthersCallTheClient()
    {
        var (provider, client) = await StartAsync("VendorApp_Cached");
        await using var _ = provider;
        var fx = provider.GetRequiredService<IFxVendorClient>();

        Assert.StartsWith("MethodCache.Generated.", fx.GetType().Namespace);

        Assert.Equal(11.5m, await fx.GetRateAsync("EUR", "SEK"));
        Assert.Equal(11.5m, await fx.GetRateAsync("EUR", "SEK"));
        Assert.Equal(1m, await fx.GetRateAsync("SEK", "SEK"));
        Assert.Equal(2, client.RateCalls);

        Assert.Equal(new[] { "EUR", "SEK" }, await fx.GetCurrenciesAsync());
        await fx.GetCurrenciesAsync();
        Assert.Equal(1, client.CurrencyCalls);

        Assert.Equal(49, fx.Square(7));
        Assert.Equal(49, fx.Square(7));
        Assert.Equal(1, client.SquareCalls);

        // Not listed, so never cached: a write such as this one must reach the client every time
        await fx.CreateOrderAsync("book");
        await fx.CreateOrderAsync("book");
        Assert.Equal(2, client.OrderCalls);
    }

    [Fact]
    public async Task Entries_UseTheInterfaceKeysAndExpandedTags()
    {
        var (provider, client) = await StartAsync("VendorApp_Tags");
        await using var _ = provider;
        var fx = provider.GetRequiredService<IFxVendorClient>();
        var cache = provider.GetRequiredService<ICacheManager>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();

        await fx.GetRateAsync("EUR", "SEK");
        await fx.GetRateAsync("USD", "SEK");

        var key = MethodCacheKeys.For(typeof(IFxVendorClient), nameof(IFxVendorClient.GetRateAsync), MethodCacheKeys.Part("EUR"), MethodCacheKeys.Part("SEK"));
        var entry = admin.GetEntry(key);
        Assert.NotNull(entry);
        Assert.Contains("fx:EUR", entry!.Tags);

        await cache.InvalidateByTagsAsync("fx:EUR");
        await fx.GetRateAsync("EUR", "SEK");
        await fx.GetRateAsync("USD", "SEK");
        Assert.Equal(3, client.RateCalls);
    }

    [Fact]
    public async Task Policies_ComeFromTheAttribute_AndConfigurationWins()
    {
        var (provider, _) = await StartAsync("VendorApp_Policies");
        await using var __ = provider;
        var methods = provider.GetRequiredService<ICacheAdministrator>().GetMethods();

        var rate = Assert.Single(methods, m => m.MethodId.EndsWith("IFxVendorClient.GetRateAsync", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMinutes(1), rate.Duration);
        Assert.Contains("fx", rate.Tags);

        var currencies = Assert.Single(methods, m => m.MethodId.EndsWith("IFxVendorClient.GetCurrenciesAsync", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromHours(1), currencies.Duration);

        Assert.DoesNotContain(methods, m => m.MethodId.EndsWith("IFxVendorClient.CreateOrderAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PropertiesIndexersAndEvents_ReachTheClient_AndNestedNamesDontCollide()
    {
        const string source = @"
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;
using Microsoft.Extensions.DependencyInjection;

[assembly: CacheInterface(typeof(SdkOne.IClient), nameof(SdkOne.IClient.GetAsync))]
[assembly: CacheInterface(typeof(SdkTwo.IClient), nameof(SdkTwo.IClient.GetAsync))]

namespace VendorApp
{
    public static class Startup
    {
        public static void Configure(IServiceCollection services, SessionVendorClient client)
        {
            services.AddSingleton<SdkOne.IClient>(client);
            services.AddSingleton<SdkTwo.IClient>(client);
            services.AddMethodCache();
        }
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(source, "VendorApp_Session");
        var client = new SessionVendorClient();
        var services = new ServiceCollection();
        testAssembly.Assembly.GetType("VendorApp.Startup")!
            .GetMethod("Configure", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { services, client });
        await using var provider = services.BuildServiceProvider();

        var one = provider.GetRequiredService<SdkOne.IClient>();
        var two = provider.GetRequiredService<SdkTwo.IClient>();
        Assert.NotEqual(one.GetType().FullName, two.GetType().FullName);

        Assert.Equal(10, await one.GetAsync(1));
        Assert.Equal(10, await one.GetAsync(1));
        Assert.Equal(10, await two.GetAsync(1));
        Assert.Equal(10, await two.GetAsync(1));
        Assert.Equal(2, client.Calls);

        Assert.Equal("eu", one.Region);
        one.Region = "us";
        Assert.Equal("us", client.Region);
        Assert.Equal("item3", one[3]);

        var raised = 0;
        EventHandler handler = (_, _) => raised++;
        one.Changed += handler;
        client.RaiseChanged();
        one.Changed -= handler;
        client.RaiseChanged();
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData("nameof(IFxVendorClient.GetMissingAsync)", "MCG011", "\"GetMissingAsync\"")]
    [InlineData("nameof(IFxVendorClient.GetOverloadedAsync)", "MCG011", "overloads")]
    [InlineData("nameof(IFxVendorClient.PingAsync)", "MCG011", "without a result")]
    public async Task ListingAMethodItCannotCache_IsAnError(string method, string id, string expected)
    {
        var source = $@"
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;

[assembly: CacheInterface(typeof(IFxVendorClient), {method.Replace("nameof(IFxVendorClient.GetMissingAsync)", "\"GetMissingAsync\"")})]
";

        var errors = await GeneratorErrorsAsync(source, "VendorApp_" + id + "_" + (uint)expected.GetHashCode());
        Assert.Contains(id, errors);
        Assert.Contains(expected.Trim('"'), errors);
    }

    [Theory]
    [InlineData("typeof(VendorApp.ILocal), \"Get\"", "declared in this project")]
    [InlineData("typeof(FxVendorClient), \"Square\"", "not an interface")]
    [InlineData("typeof(System.Collections.Generic.IList<int>), \"IndexOf\"", "generic")]
    [InlineData("typeof(IFxVendorClient)", "list the methods")]
    [InlineData("typeof(IStaticVendorClient), \"GetAsync\"", "static abstract")]
    public async Task AnInterfaceItCannotCache_IsAnError(string arguments, string expected)
    {
        var source = $@"
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;

[assembly: CacheInterface({arguments})]

namespace VendorApp
{{
    public interface ILocal
    {{
        Task<int> Get(int id);
    }}
}}
";

        var errors = await GeneratorErrorsAsync(source, "VendorApp_Type_" + (uint)expected.GetHashCode());
        Assert.Contains("MCG010", errors);
        Assert.Contains(expected, errors);
    }

    [Fact]
    public async Task ListingTheSameInterfaceTwice_IsAnError()
    {
        const string source = @"
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;

[assembly: CacheInterface(typeof(IFxVendorClient), nameof(IFxVendorClient.GetRateAsync))]
[assembly: CacheInterface(typeof(IFxVendorClient), nameof(IFxVendorClient.Square))]
";

        var errors = await GeneratorErrorsAsync(source, "VendorApp_Duplicate");
        Assert.Contains("MCG012", errors);
    }

    private async Task<string> GeneratorErrorsAsync(string source, string assemblyName)
    {
        var result = await _engine.CompileWithSourceGeneratorAsync(source, assemblyName);
        return string.Join("\n", result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString()));
    }

    private async Task<(ServiceProvider Provider, FxVendorClient Client)> StartAsync(string assemblyName)
    {
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(Source, assemblyName);
        var client = new FxVendorClient();
        var services = new ServiceCollection();
        testAssembly.Assembly.GetType("VendorApp.Startup")!
            .GetMethod("Configure", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { services, client });
        return (services.BuildServiceProvider(), client);
    }
}
