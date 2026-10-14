using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// <c>CacheWhen</c> stores only results its predicate accepts; rejected results still reach the caller.
/// </summary>
public class CacheWhenIntegrationTests
{
    private const string Source = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace CacheWhen
{
    public interface ICatalog
    {
        [Cache]
        Task<List<int>> SearchAsync(string query);

        [Cache]
        int Count(string category);

        [Cache]
        ValueTask<string?> FindAsync(int id);

        [Cache]
        Task<int> FragileAsync(int id);
    }

    public class Catalog : ICatalog
    {
        public static int Calls;
        public static List<int> NextSearch = new();
        public static int NextCount;
        public static string? NextFind;

        public Task<List<int>> SearchAsync(string query) { Calls++; return Task.FromResult(NextSearch); }
        public int Count(string category) { Calls++; return NextCount; }
        public ValueTask<string?> FindAsync(int id) { Calls++; return new ValueTask<string?>(NextFind); }
        public Task<int> FragileAsync(int id) => Task.FromResult(++Calls);
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<ICatalog, Catalog>();
            services.AddMethodCache(config =>
            {
                config.Method((ICatalog c) => c.SearchAsync(Any<string>.Value)).CacheWhen(r => r.Count > 0);
                config.Method((ICatalog c) => c.Count(Any<string>.Value)).CacheWhen(r => r > 0);
                config.Method((ICatalog c) => c.FindAsync(Any<int>.Value)).CacheWhen(r => r is not null);
                config.Method((ICatalog c) => c.FragileAsync(Any<int>.Value))
                    .Configure(o => o.WithDuration(TimeSpan.FromMinutes(1)))
                    .CacheWhen(r => throw new InvalidOperationException(""predicate bug""));
            });
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task RejectedResults_AreReturnedButNotCached()
    {
        var (catalog, type, provider) = await CreateAsync();
        await using var _ = provider;

        Set(type, "NextSearch", new List<int>());
        Assert.Empty(await Invoke<Task<List<int>>>(catalog, "SearchAsync", "x"));
        Assert.Empty(await Invoke<Task<List<int>>>(catalog, "SearchAsync", "x"));
        Assert.Equal(2, Calls(type));

        Set(type, "NextSearch", new List<int> { 1 });
        Assert.Single(await Invoke<Task<List<int>>>(catalog, "SearchAsync", "x"));
        Set(type, "NextSearch", new List<int>());
        Assert.Single(await Invoke<Task<List<int>>>(catalog, "SearchAsync", "x"));
        Assert.Equal(3, Calls(type));
    }

    [Fact]
    public async Task SyncMethods_UseTheResultType()
    {
        var (catalog, type, provider) = await CreateAsync();
        await using var _ = provider;

        Set(type, "NextCount", 0);
        Assert.Equal(0, Invoke<int>(catalog, "Count", "a"));
        Set(type, "NextCount", 5);
        Assert.Equal(5, Invoke<int>(catalog, "Count", "a"));
        Set(type, "NextCount", 7);
        Assert.Equal(5, Invoke<int>(catalog, "Count", "a"));
        Assert.Equal(2, Calls(type));
    }

    [Fact]
    public async Task ValueTaskMethods_SeeNullResults()
    {
        var (catalog, type, provider) = await CreateAsync();
        await using var _ = provider;

        Set(type, "NextFind", null);
        Assert.Null(await Invoke<ValueTask<string?>>(catalog, "FindAsync", 1));
        Set(type, "NextFind", "found");
        Assert.Equal("found", await Invoke<ValueTask<string?>>(catalog, "FindAsync", 1));
        Set(type, "NextFind", "changed");
        Assert.Equal("found", await Invoke<ValueTask<string?>>(catalog, "FindAsync", 1));
        Assert.Equal(2, Calls(type));
    }

    [Fact]
    public async Task APredicateThatThrows_DoesNotFailTheCall()
    {
        var (catalog, type, provider) = await CreateAsync();
        await using var _ = provider;

        Assert.Equal(1, await Invoke<Task<int>>(catalog, "FragileAsync", 1));
        Assert.Equal(2, await Invoke<Task<int>>(catalog, "FragileAsync", 1));
    }

    private async Task<(object Catalog, Type Type, ServiceProvider Provider)> CreateAsync()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source, "CacheWhenApp");
        var assembly = compiled.Assembly;

        var services = new ServiceCollection();
        assembly.GetType("CacheWhen.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        var type = assembly.GetType("CacheWhen.Catalog")!;
        Set(type, "Calls", 0);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService(assembly.GetType("CacheWhen.ICatalog")!), type, provider);
    }

    private static T Invoke<T>(object service, string method, object arg)
        => (T)InterfaceMethods.Find(service, method).Invoke(service, new[] { arg })!;

    private static void Set(Type type, string field, object? value)
        => type.GetField(field, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, value);

    private static int Calls(Type type)
        => (int)type.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
}
