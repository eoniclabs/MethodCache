using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// <c>SetAsync</c> stores a value the application already has. Each scenario runs on the built-in memory manager and on
/// an application manager, which gets the key invalidated and then the value through its <c>GetOrCreateAsync</c>.
/// </summary>
public sealed class SetAsyncTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public enum ManagerKind { Memory, Custom }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_StoresTheValue_SoTheNextReadDoesNotLoad(ManagerKind kind)
    {
        using var harness = new Harness(kind);

        await harness.Cache.SetAsync("user:1", "Ada", o => o.WithDuration(TimeSpan.FromMinutes(5)));

        Assert.Equal("Ada", (await harness.Cache.TryGetAsync<string>("user:1")).Value);
        Assert.Equal("Ada", await harness.Cache.GetOrCreateAsync<string>("user:1", _ => throw new InvalidOperationException("loaded")));
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_ReplacesTheCachedValue(ManagerKind kind)
    {
        using var harness = new Harness(kind);
        await harness.Cache.GetOrCreateAsync("user:1", _ => ValueTask.FromResult("Ada"), o => o.WithDuration(TimeSpan.FromMinutes(5)));

        await harness.Cache.SetAsync("user:1", "Grace", o => o.WithDuration(TimeSpan.FromMinutes(5)));

        Assert.Equal("Grace", (await harness.Cache.TryGetAsync<string>("user:1")).Value);
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_ExpiresAfterItsDuration(ManagerKind kind)
    {
        using var harness = new Harness(kind);

        await harness.Cache.SetAsync("user:1", "Ada", o => o.WithDuration(TimeSpan.FromMinutes(5)));
        harness.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False((await harness.Cache.TryGetAsync<string>("user:1")).Found);
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_DurationForGivesTheValueItsOwnLifetime(ManagerKind kind)
    {
        using var harness = new Harness(kind);

        await harness.Cache.SetAsync("token", TimeSpan.FromMinutes(2), o => o
            .WithDuration(TimeSpan.FromHours(1))
            .DurationFor<TimeSpan>(lifetime => lifetime));
        harness.Time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        Assert.False((await harness.Cache.TryGetAsync<TimeSpan>("token")).Found);
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_ValueCacheWhenTurnsDown_IsNotStoredAndRemovesTheOldValue(ManagerKind kind)
    {
        using var harness = new Harness(kind);
        await harness.Cache.SetAsync("user:1", "Ada", o => o.WithDuration(TimeSpan.FromMinutes(5)));

        await harness.Cache.SetAsync("user:1", "", o => o
            .WithDuration(TimeSpan.FromMinutes(5))
            .CacheWhen<string>(name => name.Length > 0));

        Assert.False((await harness.Cache.TryGetAsync<string>("user:1")).Found);
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_Disabled_DoesNothing(ManagerKind kind)
    {
        using var harness = new Harness(kind);
        await harness.Cache.SetAsync("user:1", "Ada", o => o.WithDuration(TimeSpan.FromMinutes(5)));

        await harness.Cache.SetAsync("user:1", "Grace", o => o.WithEnabled(false));

        Assert.Equal("Ada", (await harness.Cache.TryGetAsync<string>("user:1")).Value);
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_WithVersion_IsReadWithThatVersionOnly(ManagerKind kind)
    {
        using var harness = new Harness(kind);

        await harness.Cache.SetAsync("user:1", "Ada", o => o.WithDuration(TimeSpan.FromMinutes(5)).WithVersion(2));

        Assert.Equal("Ada", (await harness.Cache.TryGetAsync<string>("user:1", version: 2)).Value);
        Assert.False((await harness.Cache.TryGetAsync<string>("user:1")).Found);
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_CacheWhenForAnotherType_ThrowsWithReadableNames(ManagerKind kind)
    {
        using var harness = new Harness(kind);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Cache.SetAsync("count", 3, o => o
            .WithDuration(TimeSpan.FromMinutes(5))
            .CacheWhen<string>(text => text.Length > 0)).AsTask());

        Assert.Contains("CacheWhen<string> can't check the int result", error.Message);
        Assert.False((await harness.Cache.TryGetAsync<int>("count")).Found);
    }

    [Fact]
    public async Task Set_WhileALoadRuns_TheLoadDoesNotOverwriteTheSetValue()
    {
        using var harness = new Harness(ManagerKind.Memory);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = harness.Cache.GetOrCreateAsync("user:1", async _ =>
        {
            started.SetResult();
            return await gate.Task;
        }, o => o.WithDuration(TimeSpan.FromMinutes(5))).AsTask();
        await started.Task.WaitAsync(Limit);

        await harness.Cache.SetAsync("user:1", "Grace", o => o.WithDuration(TimeSpan.FromMinutes(5)));
        gate.SetResult("Ada (read before the save)");

        Assert.Equal("Ada (read before the save)", await load.WaitAsync(Limit));
        Assert.Equal("Grace", (await harness.Cache.TryGetAsync<string>("user:1")).Value);
    }

    [Theory]
    [InlineData(ManagerKind.Memory)]
    [InlineData(ManagerKind.Custom)]
    public async Task Set_EmptyKeyOrNullOptions_Throws(ManagerKind kind)
    {
        using var harness = new Harness(kind);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Cache.SetAsync("", "Ada").AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Cache.SetAsync("user:1", "Ada", (CacheEntryOptions)null!).AsTask());
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

        public void Dispose() => Memory.Dispose();
    }

    /// <summary>A manager of the application's own, which MethodCache can't see through.</summary>
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
