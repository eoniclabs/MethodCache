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
        services.AddMethodCache(b => b.Method((IReader r) => r.ReadAsync(0)).Configure(o => o.WithDistributed(false).AddTags("flag:{id}")));
        await using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IReader>();
        Assert.Equal(1, await reader.ReadAsync(1));
        Assert.Equal(1, await reader.ReadAsync(1));
        Assert.Equal(2, custom.Calls);
        Assert.Equal(1, origin.Calls);
        // The key already carries the method's version, so the options don't repeat it
        Assert.Null(custom.LastOptions!.Version);
        Assert.Contains(".ReadAsync:v2:1", custom.LastKey, StringComparison.Ordinal);
        Assert.False(custom.LastOptions.Distributed);
        Assert.Contains("id:1", custom.LastOptions.Tags);
        Assert.Contains("flag:1", custom.LastOptions.Tags);
        Assert.Equal(1, reader.Sync());
        Assert.Equal(2, reader.Sync());
        Assert.Equal(2, custom.Calls);
    }

    [Fact]
    public async Task GeneratedVersionedCall_IsFoundAndInvalidatedByItsKeyFromIMethodCacheKeys()
    {
        var custom = new FourMethodManager();
        var origin = new Origin();
        var services = new ServiceCollection();
        services.AddSingleton<ICacheManager>(custom);
        services.AddSingleton<IReader>(origin);
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IReader>();
        Assert.Equal(1, await reader.ReadAsync(1));

        var key = provider.GetRequiredService<IMethodCacheKeys>().For<IReader>(r => r.ReadAsync(1));
        Assert.Equal(custom.LastKey, key);
        Assert.Equal(key, MethodCacheKeys.StoredKey(key));
        Assert.Equal(1, (await custom.TryGetAsync<int>(key)).Value);
        await custom.InvalidateKeyAsync(key);
        Assert.False((await custom.TryGetAsync<int>(key)).Found);
        Assert.Equal(1, await reader.ReadAsync(1));
        Assert.Equal(2, origin.Calls);
    }

    [Fact]
    public async Task ExplicitOptions_UseTheSameVersionKeysAndTypedPredicateAsBuilderCalls()
    {
        ICacheManager custom = new FourMethodManager();
        var options = new CacheEntryOptions.Builder().WithVersion(4).CacheWhen<int>(value => value > 0).Build();
        Assert.Equal(1, await custom.GetOrCreateAsync("manual", _ => ValueTask.FromResult(1), options));
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
        var candidates = new TaskCompletionSource<IReadOnlyDictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bulk = wrapper.GetOrCreateManyAsync<int>(["bulk", "bulk"], (missing, _) =>
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
            (_, _) => throw new Xunit.Sdk.XunitException("The bulk factory must not run.")).AsTask());
        Assert.NotNull(manager.FirstLoad);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.FirstLoad!.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class SynchronouslyFailingManager : ICacheManager
    {
        private readonly NoOpCacheManager _noOp = new();
        public Task? FirstLoad;
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default)
            => _noOp.TryGetAsync<T>(key, version, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default)
            => _noOp.InvalidateKeysAsync(keys, version, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
            => _noOp.InvalidateTagsAsync(tags, cancellationToken);
        public ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (key == "second") throw new InvalidOperationException("Manager rejected the second load.");
            var load = factory(state, cancellationToken).AsTask();
            FirstLoad = load;
            return new(load);
        }
    }

    private sealed class PublicForwarder(ICacheManager inner) : ICacheManager
    {
        public int Calls;
        public async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory, CacheEntryOptions? options, CancellationToken token)
        {
            Calls++;
            await Task.Yield();
            return await inner.GetOrCreateAsync(key, state, factory, options, token);
        }
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken token = default) => inner.TryGetAsync<T>(key, version, token);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken token = default) => inner.InvalidateKeysAsync(keys, version, token);
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
        public string? LastKey;
        public CacheEntryOptions? LastOptions;
        public async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions? options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++; LastKey = key; LastOptions = options;
            options ??= new CacheEntryOptions.Builder().Build();
            var storedKey = MethodCacheKeys.StoredKey(key, options.Version);
            if (options.Enabled != false && _values.TryGetValue(storedKey, out var stored) && options.CanCache((T)stored!)) return (T)stored!;
            var value = await factory(state, cancellationToken);
            if (options.CanCache(value)) _values[storedKey] = value;
            return value;
        }
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(_values.TryGetValue(MethodCacheKeys.StoredKey(key, version), out var value) ? CacheLookupResult<T>.Hit((T)value!) : CacheLookupResult<T>.Miss);
        }
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = keys.Select(key => MethodCacheKeys.StoredKey(key, version)).Distinct(StringComparer.Ordinal).ToArray();
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
