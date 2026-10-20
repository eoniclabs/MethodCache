using MethodCache.Policies;
using System.Reflection;
using System.Runtime.CompilerServices;
using MethodCache;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
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
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
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
            services.AddMethodCache(config =>
            {
                config.FromAssembly(typeof(Startup).Assembly);
                config.Method((IFxVendorClient c) => c.GetCurrenciesAsync()).Configure(o => o.WithDuration(TimeSpan.FromHours(1)));
            });
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task SelectedModule_OtherModuleAlsoBindsItsOwnService_DoesNotImportItsUnselectedPolicy()
    {
        var first = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("SelectedFx", "00:00:30"), "SelectedPolicyModule");
        var second = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("OtherFx", "01:00:00") + """
            namespace OtherFx {
                public interface IAuxiliary { [MethodCache.Cache] int Read(int id); }
                public sealed class Auxiliary : IAuxiliary { public int Read(int id) => id; }
            }
            """, "OtherPolicyModule");
        var services = new ServiceCollection();
        services.AddSingleton<IFxVendorClient>(new FxVendorClient());
        services.AddSingleton(second.Assembly.GetType("OtherFx.IAuxiliary")!, second.Assembly.GetType("OtherFx.Auxiliary")!);
        services.AddMethodCache(b => b.FromAssembly(first.Assembly));
        await using var provider = services.BuildServiceProvider();
        var snapshots = new List<PolicySnapshot>();
        foreach (var source in provider.GetServices<PolicySourceRegistration>().Where(s => s.Source.SourceId == "attributes"))
            snapshots.AddRange(await source.Source.GetSnapshotAsync());
        var selected = Assert.Single(snapshots, s => s.MethodId.EndsWith("IFxVendorClient.GetRateAsync", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(30), selected.Policy.Duration);
    }

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

        var key = provider.GetRequiredService<IMethodCacheKeys>().For<IFxVendorClient>(vendor => vendor.GetRateAsync("EUR", "SEK"));
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
        Assert.Equal(TimeSpan.FromMinutes(1), rate.Options.Duration);
        Assert.Contains("fx", rate.Options.Tags);

        var currencies = Assert.Single(methods, m => m.MethodId.EndsWith("IFxVendorClient.GetCurrenciesAsync", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromHours(1), currencies.Options.Duration);

        Assert.DoesNotContain(methods, m => m.MethodId.EndsWith("IFxVendorClient.CreateOrderAsync", StringComparison.Ordinal));
    }

    // A library and the application can both list a vendor interface; the module initializer that ran last must not
    // decide whose policies the application gets
    [Fact]
    public async Task TwoAssembliesListingTheSameInterface_TheCallingAssemblysDecoratorWins()
    {
        var app = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("AppFx", "00:00:30"), "SharedVendor_App");
        var library = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("LibraryFx", "01:00:00"), "SharedVendor_Library");
        RuntimeHelpers.RunModuleConstructor(app.Assembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(library.Assembly.ManifestModule.ModuleHandle);

        var services = new ServiceCollection();
        services.AddSingleton<IFxVendorClient>(new FxVendorClient());
        app.Assembly.GetType("AppFx.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        await using var provider = services.BuildServiceProvider();

        var rate = Assert.Single(provider.GetRequiredService<ICacheAdministrator>().GetMethods(),
            m => m.MethodId.EndsWith("IFxVendorClient.GetRateAsync", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(30), rate.Options.Duration);
    }

    [Fact]
    public async Task TwoAssembliesListingTheSameInterface_AnotherCaller_GetsAClearError()
    {
        var first = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("FirstFx", "00:00:30"), "SharedVendor_First");
        var second = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("SecondFx", "01:00:00"), "SharedVendor_Second");
        RuntimeHelpers.RunModuleConstructor(first.Assembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(second.Assembly.ManifestModule.ModuleHandle);

        var services = new ServiceCollection();
        services.AddSingleton<IFxVendorClient>(new FxVendorClient());

        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCache());
        Assert.Contains("SharedVendor_First", error.Message);
        Assert.Contains("SharedVendor_Second", error.Message);
        Assert.Contains("FromAssemblyOf", error.Message);

        // Naming the assembly settles it
        services.AddMethodCache(b => b.FromAssembly(second.Assembly));
        await using var provider = services.BuildServiceProvider();
        var rate = Assert.Single(provider.GetRequiredService<ICacheAdministrator>().GetMethods(),
            m => m.MethodId.EndsWith("IFxVendorClient.GetRateAsync", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromHours(1), rate.Options.Duration);
    }

    // An assembly passed to AddMethodCache whose code hasn't run yet still has its decorator chosen, even when
    // another assembly already registered one for the interface
    [Fact]
    public async Task NamedAssemblyNotYetInitialized_ItsDecoratorWins()
    {
        var registered = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("RegisteredFx", "00:00:30"), "SharedVendor_Registered");
        var named = await _engine.CompileWithSourceGeneratorAsync(SharedVendorSource("NamedFx", "01:00:00"), "SharedVendor_Named");
        RuntimeHelpers.RunModuleConstructor(registered.Assembly.ManifestModule.ModuleHandle);

        var services = new ServiceCollection();
        services.AddSingleton<IFxVendorClient>(new FxVendorClient());
        services.AddMethodCache(b => b.FromAssembly(named.Assembly));
        await using var provider = services.BuildServiceProvider();

        var rate = Assert.Single(provider.GetRequiredService<ICacheAdministrator>().GetMethods(),
            m => m.MethodId.EndsWith("IFxVendorClient.GetRateAsync", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromHours(1), rate.Options.Duration);
    }

    private static string SharedVendorSource(string ns, string duration) => $@"
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;

[assembly: CacheInterface(typeof(IFxVendorClient), nameof(IFxVendorClient.GetRateAsync), Duration = ""{duration}"")]

namespace {ns}
{{
    public static class Startup
    {{
        public static void Configure(IServiceCollection services) => services.AddMethodCache(b => b.FromAssembly(typeof(Startup).Assembly));
    }}
}}";

    [Fact]
    public async Task PropertiesIndexersAndEvents_ReachTheClient_AndNestedNamesDontCollide()
    {
        const string source = @"
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
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
    [InlineData("nameof(IFxVendorClient.GetMissingAsync)", "MC0012", "\"GetMissingAsync\"")]
    [InlineData("nameof(IFxVendorClient.GetOverloadedAsync)", "MC0012", "overloads")]
    [InlineData("nameof(IFxVendorClient.PingAsync)", "MC0012", "without a result")]
    public async Task ListingAMethodItCannotCache_IsAnError(string method, string id, string expected)
    {
        var source = $@"
using MethodCache;
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
using MethodCache;
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
        Assert.Contains("MC0011", errors);
        Assert.Contains(expected, errors);
    }

    [Fact]
    public async Task ListingTheSameInterfaceTwice_IsAnError()
    {
        const string source = @"
using MethodCache;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;

[assembly: CacheInterface(typeof(IFxVendorClient), nameof(IFxVendorClient.GetRateAsync))]
[assembly: CacheInterface(typeof(IFxVendorClient), nameof(IFxVendorClient.Square))]
";

        var errors = await GeneratorErrorsAsync(source, "VendorApp_Duplicate");
        Assert.Contains("MC0013", errors);
    }

    [Fact]
    public async Task TagProblems_AreReportedOnTheAttribute()
    {
        const string source = @"
using MethodCache;
using MethodCache.SourceGenerator.IntegrationTests.Vendor;

[assembly: CacheInterface(typeof(IFxVendorClient), nameof(IFxVendorClient.GetRateAsync), Tags = new[] { ""fx:{form}"", ""fx:{from.Length}"" })]
";

        var result = await _engine.CompileWithSourceGeneratorAsync(source, "VendorApp_Tags");
        var warnings = result.Diagnostics.Where(d => d.Id is "MC0010" or "MC0016").ToList();
        Assert.Equal(new[] { "MC0010", "MC0016" }, warnings.Select(d => d.Id).OrderBy(id => id));
        Assert.All(warnings, d => Assert.True(d.Location.IsInSource));
    }

    [Fact]
    public async Task AttributeInALibraryWhoseCodeHasNotRun_StillDecoratesTheClient()
    {
        // A library (think Infrastructure.dll) that only holds [assembly: CacheInterface] for a vendor interface. It is
        // loaded, but nothing in it has run, so its module initializer has not registered the decorator yet.
        var library = await _engine.CompileWithSourceGeneratorAsync("""
            [assembly: MethodCache.CacheInterface(typeof(MethodCache.SourceGenerator.IntegrationTests.Vendor.IClockVendorClient), "Get")]
            namespace Infrastructure { public static class Marker { } }
            """, "VendorLibrary_" + Guid.NewGuid().ToString("N"));
        Assert.Contains(library.GeneratedSources.Values, s => s.Contains("IClockVendorClient", StringComparison.Ordinal));

        // The application registers the vendor client and calls AddMethodCache() itself
        var services = new ServiceCollection();
        services.AddSingleton<IClockVendorClient, ClockVendorClient>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        Assert.IsNotType<ClockVendorClient>(provider.GetRequiredService<IClockVendorClient>());
    }

    [Fact]
    public async Task LookupWhileAnotherThreadRunsTheLibrarysInitializer_WaitsForItAndFindsTheDecorator()
    {
        // The library's own module initializer runs before the generated one and holds until the test opens the gate
        var library = await _engine.CompileWithSourceGeneratorAsync("""
            [assembly: MethodCache.CacheInterface(typeof(MethodCache.SourceGenerator.IntegrationTests.Vendor.IGatedVendorClient), "Get")]
            namespace Infrastructure
            {
                internal static class Gate
                {
                    [System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Hold() => MethodCache.SourceGenerator.IntegrationTests.Vendor.ModuleInitializerGate.Wait();
                }
            }
            """, "GatedLibrary_" + Guid.NewGuid().ToString("N"));
        Assert.Contains(library.GeneratedSources.Values, s => s.Contains("IGatedVendorClient", StringComparison.Ordinal));

        static bool Find(Assembly assembly) => MethodCache.Generated.GeneratedCode.Describe([assembly], [])
            .Where(m => m.Module.GetType().Assembly == assembly).SelectMany(m => m.Decorators)
            .Any(d => d.ServiceType == typeof(IGatedVendorClient));
        var first = Task.Run(() => Find(library.Assembly));
        Assert.True(await ModuleInitializerGate.Entered.WaitAsync(TimeSpan.FromSeconds(30)), "the library's initializer did not run");
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(() => { secondStarted.SetResult(); return Find(library.Assembly); });
        await secondStarted.Task;
        try { Assert.False(first.IsCompleted); }
        finally { ModuleInitializerGate.Open.Set(); }
        Assert.True(await first);
        Assert.True(await second);
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
