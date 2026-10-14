using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrozenVendor.Contract1;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.CompatTests;

public class VendorCompatibilityTests
{
    [Fact]
    public void FrozenImages_MatchRecordedHashes()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1/manifest.json")));
        Assert.Matches("^[0-9a-f]{40}$", manifest.RootElement.GetProperty("source_commit").GetString()!);
        Assert.DoesNotContain("candidate", manifest.RootElement.GetProperty("package_version").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", manifest.RootElement.GetProperty("core_net8_sha256").GetString()!);
        Assert.Matches("^[0-9a-f]{64}$", manifest.RootElement.GetProperty("generator_sha256").GetString()!);
        foreach (var image in manifest.RootElement.GetProperty("images").EnumerateArray())
        {
            var name = image.GetProperty("file").GetString()!;
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1", name);
            Assert.Equal(image.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }
    }

    [Fact]
    public void FrozenModule_UnknownContract_LoadsAndFailsAtStartupBeforeDescribe()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1/FrozenVendor.Contract99.dll"));
        var module = assembly.GetType("FrozenVendor.Contract99.IncompatibleModule")!;
        var calls = module.GetField("DescribeCalls")!;
        try
        {
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            Assert.Equal(0, calls.GetValue(null));
            var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMethodCache());
            Assert.Contains("FrozenVendor.Contract99", error.Message);
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
    public async Task FrozenVendor_CurrentRuntime_PreservesReturnShapesTagsFallbackAndGenerics()
    {
        var inner = new Service();
        var sdk = new FrozenVendor.ThirdParty.Client();
        var services = new ServiceCollection();
        services.AddSingleton<IService>(inner);
        services.AddSingleton<FrozenVendor.ThirdParty.IClient>(sdk);
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
        var client = provider.GetRequiredService<FrozenVendor.ThirdParty.IClient>();
        Assert.Equal(1, await client.ReadAsync(42));
        Assert.Equal(1, await client.ReadAsync(42));
        Assert.Equal(2, await client.UncachedAsync(42));
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<string>>();
        Assert.Equal("1", await repository.ReadAsync(42));
        Assert.Equal("1", await repository.ReadAsync(42));
    }

    [Fact]
    public async Task FrozenVendor_KeyedScopedBindings_RemainIsolated()
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
