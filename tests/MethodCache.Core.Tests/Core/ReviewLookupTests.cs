using MethodCache.Extensibility;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class ReviewLookupTests
{
    public interface IReader { [Cache(Tags = new[] { "users" })] Task<int> Read(int id); }
    public sealed class Reader : IReader { public Task<int> Read(int id) => Task.FromResult(id); }

    public interface IKeyedGeneric { [Cache(Version = 2)] int Read<T>(int id); }
    public sealed class KeyedGeneric : IKeyedGeneric { public int Read<T>(int id) => id; }

    [Fact]
    public async Task KeyedGenericMethod_UsesThePublicGenericKeyedVersionGrammar()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedGeneric, KeyedGeneric>("ecb");
        services.AddMethodCache(builder => builder.FromAssemblyOf<ReviewLookupTests>());
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(42, provider.GetRequiredKeyedService<IKeyedGeneric>("ecb").Read<int>(42));
        var key = MethodCacheKeys.ForVersion(typeof(IKeyedGeneric),
            MethodCacheKeys.KeyedMethodName("Read<" + MethodCacheKeys.TypePart(typeof(int)) + ">", "ecb"), 2, MethodCacheKeys.Part(42));
        Assert.NotNull(provider.GetRequiredService<ICacheAdministrator>().GetEntry(key));
    }

    private static ServiceProvider Node(AtomicTestCache backend)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReader, Reader>();
        services.AddMethodCache(builder => builder.FromAssemblyOf<ReviewLookupTests>());
        services.AddMethodCacheDistributedCache(_ => backend);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task LookupGeneratedKey_UsesPolicyIdentityWithoutResolvingService()
    {
        var backend = new AtomicTestCache();
        await using var writer = Node(backend);
        await using var reader = Node(backend);
        Assert.Equal(42, await writer.GetRequiredService<IReader>().Read(42));
        var key = MethodCacheKeys.For(typeof(IReader), "Read", MethodCacheKeys.Part(42));
        var result = await reader.GetRequiredService<ICacheManager>().TryGetAsync<int>(key, default);
        Assert.True(result.Found);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task LookupManualL2Entry_PreservesStoredTagsWhenPromotingToL1()
    {
        var backend = new AtomicTestCache();
        await using var writer = Node(backend);
        await using var reader = Node(backend);
        await writer.GetRequiredService<ICacheManager>().GetOrCreateAsync("manual", _ => new ValueTask<int>(42), options => options.WithTags("users"));
        var cache = reader.GetRequiredService<ICacheManager>();
        Assert.True((await cache.TryGetAsync<int>("manual", default)).Found);
        var admin = reader.GetRequiredService<ICacheAdministrator>();
        Assert.Contains("users", admin.GetEntry("manual")!.Tags);
        await cache.InvalidateTagsAsync(["users"], default);
        Assert.False((await cache.TryGetAsync<int>("manual", default)).Found);
    }

    [Fact]
    public async Task PolicyChange_StillInvalidatesOwnedCacheWhenInterfaceIsDecorated()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReader, Reader>();
        services.AddMethodCache(builder => builder.FromAssemblyOf<ReviewLookupTests>());
        services.AddSingleton<ICacheManager>(provider => new ForwardingManager(provider.GetRequiredService<InMemoryCacheManager>()));
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IReader>();
        await service.Read(42);
        var cache = provider.GetRequiredService<InMemoryCacheManager>();
        var key = MethodCacheKeys.For(typeof(IReader), "Read", MethodCacheKeys.Part(42));
        Assert.True((await cache.TryGetAsync<int>(key, default)).Found);
        await provider.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodIds.For(typeof(IReader), "Read"), options => options.WithVersion(2));
        Assert.False((await cache.TryGetAsync<int>(key, default)).Found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task LogicalLeadingNul_UsesTheSameFramingAcrossAllOperations(int? version)
    {
        await using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        const string key = "\0v2:logical";
        await cache.GetOrCreateAsync(key, _ => new ValueTask<int>(42), builder => { if (version is { } number) builder.WithVersion(number); });
        Assert.Equal(42, (await cache.TryGetAsync<int>(key, version, default)).Value);
        await cache.InvalidateKeysAsync([key], version, default);
        Assert.False((await cache.TryGetAsync<int>(key, version, default)).Found);
    }

    private sealed class ForwardingManager(ICacheManager inner) : NoOpCacheManager
    {
        public override ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory, CacheEntryOptions? options, CancellationToken cancellationToken)
            => inner.GetOrCreateAsync(key, factory, options, cancellationToken);
    }
}
