using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.Core.Tests.Core.Extensions;

public class BulkInvalidationReviewTests
{
    [Fact]
    public async Task BulkLoad_ThroughAsyncDecorator_PreservesDecoratorAndInvalidation()
    {
        using var cache = new InMemoryCacheManager();
        var decorated = new CountingDecorator(cache);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = decorated.GetOrCreateManyAsync<string>(["key"], (_, _) =>
        {
            started.SetResult();
            return new(candidates.Task);
        }, o => o.AddTags("tag")).AsTask();
        await started.Task;
        await decorated.InvalidateByTagsAsync("tag");
        candidates.SetResult(new Dictionary<string, string> { ["key"] = "old" });
        Assert.Equal("old", (await batch)["key"]);
        Assert.Equal(1, decorated.Calls);
        Assert.False((await decorated.TryGetAsync<string>("key")).Found);
    }

    [Fact]
    public async Task BulkLoad_FactoryThrows_ReleasesPublicationGuards()
    {
        using var cache = new InMemoryCacheManager();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetOrCreateManyAsync<string>(["key"],
            (_, _) => throw new InvalidOperationException("source failed")));
        await cache.InvalidateByKeysAsync("key");
        var fresh = await cache.GetOrCreateManyAsync<string>(["key"], (_, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["key"] = "new" }));
        Assert.Equal("new", fresh["key"]);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("tag")]
    [InlineData("clear")]
    public async Task BulkLoad_InvalidatedDuringFactory_DoesNotPublishOldValue(string invalidation)
    {
        using var cache = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = cache.GetOrCreateManyAsync<string>(["key"], (_, _) =>
        {
            started.SetResult();
            return new ValueTask<IReadOnlyDictionary<string, string>>(result.Task);
        }, o => o.AddTags("tag")).AsTask();
        await started.Task;

        if (invalidation == "key") await cache.InvalidateByKeysAsync("key");
        else if (invalidation == "tag") await cache.InvalidateByTagsAsync("tag");
        else await cache.ClearAsync();

        result.SetResult(new Dictionary<string, string> { ["key"] = "before-invalidation" });
        await load;
        Assert.False((await cache.TryGetAsync<string>("key")).Found);
    }

    private sealed class CountingDecorator(ICacheManager inner) : ICacheManager, ICacheManagerDecorator
    {
        public async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory, CacheEntryOptions? options, CancellationToken token)
        { Calls++; await Task.Yield(); return await inner.GetOrCreateAsync(key, state, factory, options, token); }
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken token = default) => inner.TryGetAsync<T>(key, version, token);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken token = default) => inner.InvalidateKeysAsync(keys, version, token);
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken token) => inner.InvalidateTagsAsync(tags, token);

        public int Calls;
        public ICacheManager InnerManager => inner;
        public async Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            return await inner.GetOrCreateFastAsync(cacheKey, methodName, factory, policy, cancellationToken);
        }
        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => inner.GetOrCreateAsync(methodName, args, factory, policy, keyGenerator);
        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => inner.TryGetAsync<T>(methodName, args, policy, keyGenerator);
        public ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => inner.LookupAsync<T>(methodName, args, policy, keyGenerator);
        public ValueTask<T?> TryGetFastAsync<T>(string key) => inner.TryGetFastAsync<T>(key);
        public Task InvalidateByKeysAsync(params string[] keys) => inner.InvalidateByKeysAsync(keys);
        public Task InvalidateByTagsAsync(params string[] tags) => inner.InvalidateByTagsAsync(tags);
        public Task InvalidateByTagPatternAsync(string pattern) => inner.InvalidateByTagPatternAsync(pattern);
    }
}
