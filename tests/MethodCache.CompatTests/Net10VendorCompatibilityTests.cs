using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FrozenVendor.Contract1Net10;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.CompatTests;

// Keep the unknown-contract module facts in the same xUnit class/collection as
// the historical baseline: its deliberate global registration must be isolated.
public partial class VendorCompatibilityTests
{
    [Fact]
    public void FrozenNet10Images_MatchRecordedHashesAndTarget()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1Net10/manifest.json")));
        Assert.Equal("net10.0", manifest.RootElement.GetProperty("target_framework").GetString());
        Assert.Matches("^[0-9a-f]{40}$", manifest.RootElement.GetProperty("source_commit").GetString()!);
        Assert.DoesNotContain("candidate", manifest.RootElement.GetProperty("package_version").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", manifest.RootElement.GetProperty("core_sha256").GetString()!);
        Assert.Matches("^[0-9a-f]{64}$", manifest.RootElement.GetProperty("generator_sha256").GetString()!);
        foreach (var image in manifest.RootElement.GetProperty("images").EnumerateArray())
        {
            var name = image.GetProperty("file").GetString()!;
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1Net10", name);
            Assert.Equal(image.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }
    }

    [Fact]
    public void FrozenNet10Module_UnknownContract_FailsBeforeDescribe()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1Net10/FrozenVendor.Contract99Net10.dll"));
        var module = assembly.GetType("FrozenVendor.Contract99Net10.IncompatibleModule")!;
        var calls = module.GetField("DescribeCalls")!;
        try
        {
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            Assert.Equal(0, calls.GetValue(null));
            var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMethodCache());
            Assert.Contains("FrozenVendor.Contract99Net10", error.Message);
            Assert.Contains("contract 99", error.Message);
            Assert.Equal(0, calls.GetValue(null));
        }
        finally
        {
            typeof(MethodCache.Generated.GeneratedCode).GetMethod("Unregister", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [module]);
        }
    }

    [Fact]
    public async Task FrozenNet10Vendor_CurrentRuntime_PreservesInlineFallbackTagsAndReturnShapes()
    {
        var inner = new Service();
        var sdk = new FrozenVendor.ThirdPartyNet10.Client();
        var services = new ServiceCollection();
        services.AddSingleton<IService>(inner);
        services.AddSingleton<FrozenVendor.ThirdPartyNet10.IClient>(sdk);
        services.AddScoped<IRepository<string>, StringRepository>();
        services.AddMethodCache(b => b.FromAssemblyOf<Marker>());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var service = provider.GetRequiredService<IService>();
        Assert.Equal(1, service.Read(42));
        Assert.Equal(1, service.Read(42));
        Assert.Equal(2, await service.ReadAsync(42));
        Assert.Equal(2, await service.ReadAsync(42));
        Assert.Equal(3, await service.ReadValueAsync(42));
        Assert.Equal(3, await service.ReadValueAsync(42));
        var query = new Query("\ud800:a\\b", [1, 2]);
        Assert.Equal(4, await service.SearchAsync(query));
        Assert.Equal(4, await service.SearchAsync(new Query(query.Name, [1, 2])));
        Assert.Equal("value", await service.EchoAsync("value"));
        Assert.Equal("value", await service.EchoAsync("value"));
        Assert.Equal(5, inner.Calls);
        await service.UpdateAsync(42);
        Assert.Equal(6, service.Read(42));
        var client = provider.GetRequiredService<FrozenVendor.ThirdPartyNet10.IClient>();
        Assert.Equal(1, await client.ReadAsync(42));
        Assert.Equal(1, await client.ReadAsync(42));
        Assert.Equal(2, await client.UncachedAsync(42));
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<string>>();
        Assert.Equal("1", await repository.ReadAsync(42));
        Assert.Equal("1", await repository.ReadAsync(42));
    }

    [Fact]
    public async Task FrozenNet10Vendor_KeyedScopedBindings_RemainIsolated()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IRepository<string>, StringRepository>("a");
        services.AddKeyedScoped<IRepository<string>, StringRepository>("b");
        services.AddMethodCache(b => b.FromAssemblyOf<Marker>());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var first = scope.ServiceProvider.GetRequiredKeyedService<IRepository<string>>("a");
        var second = scope.ServiceProvider.GetRequiredKeyedService<IRepository<string>>("b");
        Assert.Equal("1", await first.ReadAsync(42));
        Assert.Equal("1", await first.ReadAsync(42));
        Assert.Equal("1", await second.ReadAsync(42));
    }
}
