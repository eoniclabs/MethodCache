using System.Collections.Concurrent;
using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// GetOrCreateManyAsync must behave as GetOrCreateAsync called once per key. Each scenario runs through both paths,
/// on the built-in memory manager and on an application manager (which takes the bulk path for custom managers),
/// and asserts the same outcome.
/// </summary>
public sealed class BulkSingleEquivalenceTests
{
    // Bounds a wait that must end; a hang fails the test instead of blocking the run
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public enum CallPath { Single, Bulk }

    public enum ManagerKind { Memory, Custom }

    public static TheoryData<ManagerKind, CallPath> AllPaths => new()
    {
        { ManagerKind.Memory, CallPath.Single },
        { ManagerKind.Memory, CallPath.Bulk },
        { ManagerKind.Custom, CallPath.Single },
        { ManagerKind.Custom, CallPath.Bulk },
    };

    private static readonly CacheEntryOptions Cached = new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromMinutes(5)).Build();

    private static readonly CacheEntryOptions FailSafe = new CacheEntryOptions.Builder()
        .WithDuration(TimeSpan.FromMinutes(1)).WithFailSafe(TimeSpan.FromMinutes(10)).Build();

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task Load_CallerCancelsWhileFactoryIgnoresToken_ThrowsPromptlyAndCachesTheLateResult(ManagerKind kind, CallPath path)
    {
        using var harness = new Harness(kind);
        var source = new Source(key => key + "1") { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously), StartsToSignal = 2 };
        using var cancellation = new CancellationTokenSource();
        var load = LoadAsync(harness.Cache, path, ["a", "b"], source, Cached, cancellation.Token);
        await source.AllStarted.Task.WaitAsync(Limit);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(Limit));
        source.Gate.SetResult();
        // The factory finishes after its caller left, and its result is cached as a single load's is
        Assert.True(SpinWait.SpinUntil(() => harness.IsCached("a") && harness.IsCached("b"), Limit));
        Assert.Equal("a1", (await harness.Cache.TryGetAsync<string>("a")).Value);
        Assert.Equal(1, source.Loads["a"]);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task Load_FactoryFailsWithStaleValues_ServesThemUntilRetryAfter(ManagerKind kind, CallPath path)
    {
        using var harness = new Harness(kind);
        await LoadAsync(harness.Cache, path, ["a", "b"], new Source(key => key + "1"), FailSafe);
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        var failing = new Source(key => key + "2") { Failure = new InvalidOperationException("source down") };

        var served = await LoadAsync(harness.Cache, path, ["a", "b"], failing, FailSafe);
        var reused = await LoadAsync(harness.Cache, path, ["a", "b"], failing, FailSafe);
        harness.Time.Advance(TimeSpan.FromSeconds(31)); // past the default FailSafeRetryAfter
        var reloaded = await LoadAsync(harness.Cache, path, ["a", "b"], new Source(key => key + "3"), FailSafe);

        Assert.Equal(Values(("a", "a1"), ("b", "b1")), served);
        Assert.Equal(Values(("a", "a1"), ("b", "b1")), reused);
        Assert.Equal(1, failing.Loads["a"]);
        Assert.Equal(1, failing.Loads["b"]);
        Assert.Equal(Values(("a", "a3"), ("b", "b3")), reloaded);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task Load_FactoryFailsAndOneKeyHasNoStaleValue_ThrowsTheFactoryErrorAndKeepsTheOtherStaleValue(ManagerKind kind, CallPath path)
    {
        using var harness = new Harness(kind);
        await LoadAsync(harness.Cache, path, ["a"], new Source(key => key + "1"), FailSafe);
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        var failure = new InvalidOperationException("source down");
        var unused = new Source(key => key + "3");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LoadAsync(harness.Cache, path, ["a", "c"], new Source(key => key + "2") { Failure = failure }, FailSafe));
        var afterwards = await LoadAsync(harness.Cache, path, ["a"], unused, FailSafe);

        Assert.Same(failure, thrown);
        Assert.Equal(Values(("a", "a1")), afterwards);
        Assert.Empty(unused.Loads);
        Assert.False((await harness.Cache.TryGetAsync<string>("c")).Found);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task Load_CacheWhenRejectsOneValue_ReturnsBothAndCachesOnlyTheAccepted(ManagerKind kind, CallPath path)
    {
        using var harness = new Harness(kind);
        var options = new CacheEntryOptions.Builder().CacheWhen<string>(value => value != "reject").Build();

        var values = await LoadAsync(harness.Cache, path, ["a", "b"], new Source(key => key == "a" ? "reject" : "keep"), options);

        Assert.Equal(Values(("a", "reject"), ("b", "keep")), values);
        Assert.False((await harness.Cache.TryGetAsync<string>("a")).Found);
        Assert.Equal("keep", (await harness.Cache.TryGetAsync<string>("b")).Value);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task Load_CachingDisabled_CallsTheFactoryEveryTimeAndCachesNothing(ManagerKind kind, CallPath path)
    {
        using var harness = new Harness(kind);
        await LoadAsync(harness.Cache, path, ["a", "b"], new Source(key => key + "0"), Cached);
        var options = new CacheEntryOptions.Builder().WithEnabled(false).Build();
        var source = new Source(key => key + "1");

        var first = await LoadAsync(harness.Cache, path, ["a", "b"], source, options);
        var second = await LoadAsync(harness.Cache, path, ["a", "b"], source, options);

        Assert.Equal(Values(("a", "a1"), ("b", "b1")), first);
        Assert.Equal(first, second);
        Assert.Equal(2, source.Loads["a"]);
        Assert.Equal(2, source.Loads["b"]);
        Assert.Equal("a0", (await harness.Cache.TryGetAsync<string>("a")).Value);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task Load_KeyInvalidatedWhileLoading_ReturnsTheValueButDoesNotCacheIt(ManagerKind kind, CallPath path)
    {
        using var harness = new Harness(kind);
        var source = new Source(key => key + "1") { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously), StartsToSignal = 2 };
        var load = LoadAsync(harness.Cache, path, ["a", "b"], source, Cached);
        await source.AllStarted.Task.WaitAsync(Limit);

        await harness.Cache.InvalidateKeyAsync("a");
        source.Gate.SetResult();
        var values = await load.WaitAsync(Limit);

        Assert.Equal(Values(("a", "a1"), ("b", "b1")), values);
        Assert.False((await harness.Cache.TryGetAsync<string>("a")).Found);
        Assert.Equal("b1", (await harness.Cache.TryGetAsync<string>("b")).Value);
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadAsync(ICacheManager cache, CallPath path, string[] keys,
        Source source, CacheEntryOptions options, CancellationToken cancellationToken = default)
    {
        if (path == CallPath.Bulk)
        {
            return await cache.GetOrCreateManyAsync<string>(keys, (missing, token) => new(source.LoadAsync(missing, token)), options, cancellationToken);
        }

        var loads = keys.Select(key => cache.GetOrCreateAsync(key,
            async token => (await source.LoadAsync([key], token))[key], options, cancellationToken).AsTask()).ToArray();
        var values = await Task.WhenAll(loads);
        return keys.Select((key, index) => (key, values[index])).ToDictionary(pair => pair.key, pair => pair.Item2, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>A data source that counts the keys it loads and ignores the cancellation token on purpose.</summary>
    private sealed class Source(Func<string, string> value)
    {
        private int _started;

        public ConcurrentDictionary<string, int> Loads { get; } = new(StringComparer.Ordinal);
        public Exception? Failure { get; init; }
        public TaskCompletionSource? Gate { get; init; }
        public int StartsToSignal { get; init; } = int.MaxValue;
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyDictionary<string, string>> LoadAsync(IReadOnlyList<string> keys, CancellationToken ignored)
        {
            foreach (var key in keys) Loads.AddOrUpdate(key, 1, static (_, count) => count + 1);
            if (Interlocked.Add(ref _started, keys.Count) >= StartsToSignal) AllStarted.TrySetResult();
            if (Gate != null) await Gate.Task;
            if (Failure != null) throw Failure;
            return keys.ToDictionary(key => key, value, StringComparer.Ordinal);
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(ManagerKind kind)
        {
            Memory = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(
                new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), Time);
            Cache = kind == ManagerKind.Memory ? Memory : new ApplicationManager(Memory);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public InMemoryCacheManager Memory { get; }
        public ICacheManager Cache { get; }

        public bool IsCached(string key) => Memory.TryGetFreshForLookup<string>(key).Found;

        public void Dispose() => Memory.Dispose();
    }

    /// <summary>
    /// A manager of the application's own, which MethodCache can't see through, so bulk calls take the custom-manager
    /// path. It keeps its entries in a memory manager to have the same fail-safe, predicate and invalidation rules.
    /// </summary>
    private sealed class ApplicationManager(ICacheManager inner) : ICacheManager
    {
        public ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
            => inner.GetOrCreateAsync(key, state, factory, options, cancellationToken);

        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default)
            => inner.TryGetAsync<T>(key, version, cancellationToken);

        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default)
            => inner.InvalidateKeysAsync(keys, version, cancellationToken);

        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
            => inner.InvalidateTagsAsync(tags, cancellationToken);
    }
}
