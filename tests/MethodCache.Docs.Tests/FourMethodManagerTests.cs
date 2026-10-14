using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class FourMethodManagerTests
{
    [Fact]
    public async Task GeneratedCalls_UseOnlyFourOperations_AndPassImmutableOptions()
    {
        var custom = new FourMethodManager();
        var origin = new Origin();
        var services = new ServiceCollection();
        services.AddSingleton<ICacheManager>(custom);
        services.AddSingleton<IReader>(origin);
        services.AddMethodCache(b => b.Method((IReader r) => r.ReadAsync(0)).Configure(o => o.WithDistributed(false).WithTags("flag:{id}")));
        await using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IReader>();
        Assert.Equal(1, await reader.ReadAsync(1));
        Assert.Equal(1, await reader.ReadAsync(1));
        Assert.Equal(2, custom.Calls);
        Assert.Equal(1, origin.Calls);
        Assert.Equal(2, custom.LastOptions!.Version);
        Assert.False(custom.LastOptions.Distributed);
        Assert.Contains("id:1", custom.LastOptions.Tags);
        Assert.Contains("flag:1", custom.LastOptions.Tags);
        Assert.Equal(1, reader.Sync());
        Assert.Equal(2, reader.Sync());
        Assert.Equal(2, custom.Calls);
    }

    [Fact]
    public async Task ExplicitOptions_UseTheSameVersionKeysAndTypedPredicateAsBuilderCalls()
    {
        ICacheManager custom = new FourMethodManager();
        var options = new CacheEntryOptions.Builder().WithVersion(4).CacheWhen<int>(value => value > 0).Build();
        Assert.Equal(1, await custom.GetOrCreateAsync("manual", (context, _) => { Assert.Equal("manual", context.Key); return ValueTask.FromResult(1); }, options));
        Assert.Equal(1, (await custom.TryGetAsync<int>("manual", version: 4)).Value);
        Assert.False((await custom.TryGetAsync<int>("manual")).Found);
        await custom.InvalidateKeyAsync("manual", version: 4);
        Assert.False((await custom.TryGetAsync<int>("manual", version: 4)).Found);
    }

    [Fact]
    public async Task BulkThroughPublicForwarder_RetainsDecoratorAndInFlightInvalidation()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var inner = provider.GetRequiredService<ICacheManager>();
        var wrapper = new PublicForwarder(inner);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new TaskCompletionSource<IDictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bulk = wrapper.GetOrCreateManyAsync<int>(["bulk", "bulk"], (missing, _, _) =>
        {
            Assert.Equal(new[] { "bulk" }, missing);
            started.SetResult();
            return new(candidates.Task);
        }).AsTask();
        await started.Task;
        await wrapper.InvalidateKeyAsync("bulk");
        candidates.SetResult(new Dictionary<string, int> { ["bulk"] = 1 });
        Assert.Equal(1, (await bulk)["bulk"]);
        Assert.Equal(1, wrapper.Calls);
        Assert.False((await inner.TryGetAsync<int>("bulk")).Found);
    }

    [Fact]
    public async Task Bulk_WhenManagerThrowsSynchronously_ReleasesEarlierLoads()
    {
        var manager = new SynchronouslyFailingManager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetOrCreateManyAsync<int>(["first", "second"],
            (_, _, _) => throw new Xunit.Sdk.XunitException("The bulk factory must not run.")).AsTask());
        Assert.NotNull(manager.FirstLoad);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.FirstLoad!.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class SynchronouslyFailingManager : NoOpCacheManager
    {
        public Task? FirstLoad;
        public override ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions? options, CancellationToken cancellationToken)
        {
            if (key == "second") throw new InvalidOperationException("Manager rejected the second load.");
            var load = factory(new CacheContext(key), cancellationToken).AsTask();
            FirstLoad = load;
            return new(load);
        }
    }

    private sealed class PublicForwarder(ICacheManager inner) : ICacheManager
    {
        public int Calls;
        public async ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory, CacheEntryOptions? options, CancellationToken token)
        {
            Calls++;
            await Task.Yield();
            return await inner.GetOrCreateAsync(key, factory, options, token);
        }
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, CancellationToken token) => inner.TryGetAsync<T>(key, token);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, CancellationToken token) => inner.InvalidateKeysAsync(keys, token);
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken token) => inner.InvalidateTagsAsync(tags, token);
    }

    public interface IReader
    {
        [Cache(Duration = "00:05:00", Version = 2, Tags = ["id:{id}"])] Task<int> ReadAsync(int id);
        [Cache] int Sync();
    }
    public sealed class Origin : IReader
    {
        public int Calls;
        private int _sync;
        public Task<int> ReadAsync(int id) { Calls++; return Task.FromResult(id); }
        public int Sync() => ++_sync;
    }

    // A deliberately small test manager. Production managers also implement their own TTL and concurrency guarantees.
    private sealed class FourMethodManager : ICacheManager
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        public int Calls;
        public CacheEntryOptions? LastOptions;
        public async ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions? options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++; LastOptions = options;
            options ??= new CacheEntryOptions.Builder().Build();
            var storedKey = options.GetCacheKey(key);
            if (options.Enabled != false && _values.TryGetValue(storedKey, out var stored) && options.CanCache((T)stored!)) return (T)stored!;
            var value = await factory(new CacheContext(key), cancellationToken);
            if (options.CanCache(value)) _values[storedKey] = value;
            return value;
        }
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(_values.TryGetValue(key, out var value) ? CacheLookupResult<T>.Hit((T)value!) : CacheLookupResult<T>.Miss);
        }
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = keys.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var key in targets) _values.Remove(key);
            return new(new InvalidationResult { Requested = targets.Length, Local = targets.Length });
        }
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = tags.Distinct(StringComparer.Ordinal).Count();
            return new(new InvalidationResult { Requested = count, Local = count });
        }
    }
}
