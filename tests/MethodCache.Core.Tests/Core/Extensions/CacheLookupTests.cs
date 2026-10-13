using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core.Extensions;

/// <summary>Lookups that must see every cached entry, and the messages and defaults around them.</summary>
public class CacheLookupTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private InMemoryCacheManager CreateManager(Action<MethodCacheMemoryOptions>? configure = null)
    {
        var options = new MethodCacheMemoryOptions { EnableBackgroundCleanup = false };
        configure?.Invoke(options);
        return new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(options), _time);
    }

    // ---------- Fluent TryGetAsync / bulk report Miss for cached entries ----------

    [Fact]
    public async Task TryGetAsync_EntryInsideItsRefreshWindow_IsFound()
    {
        using var cache = CreateManager();
        await cache.GetOrCreateAsync("k", (_, _) => new ValueTask<string>("v"),
            o => o.WithDuration(TimeSpan.FromMinutes(10)).RefreshAhead(TimeSpan.FromMinutes(1)));

        _time.Advance(TimeSpan.FromSeconds(570)); // fresh, in the last minute

        (await cache.TryGetAsync<string>("k")).Found.Should().BeTrue("the entry is in memory and not expired");
    }

    [Fact]
    public async Task TryGetAsync_FastPathOff_FindsTheCachedEntry()
    {
        using var cache = CreateManager(o => o.EnableFastPath = false);
        await cache.GetOrCreateAsync("k", (_, _) => new ValueTask<string>("v"));

        (await cache.TryGetAsync<string>("k")).Found.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateManyAsync_FastPathOff_DoesNotReloadCachedKeys()
    {
        using var cache = CreateManager(o => o.EnableFastPath = false);
        await cache.GetOrCreateAsync("k", (_, _) => new ValueTask<string>("v"));

        var requested = new List<string>();
        await cache.GetOrCreateManyAsync<string>(new[] { "k" }, (keys, _, _) =>
        {
            requested.AddRange(keys);
            return new ValueTask<IDictionary<string, string>>(keys.ToDictionary(k => k, _ => "fresh"));
        });

        requested.Should().BeEmpty("\"k\" is cached");
    }

    // ---------- WithDuration's message promises "no expiration" ----------

    [Theory]
    [InlineData(5, 1, null)]
    [InlineData(1, 1, null)]
    [InlineData(1, null, 1)]
    public void Build_RefreshAheadNotShorterThanTheLifetime_Throws(int refreshMinutes, int? durationMinutes, int? slidingMinutes)
    {
        var builder = new CacheEntryOptions.Builder().RefreshAhead(TimeSpan.FromMinutes(refreshMinutes));
        if (durationMinutes is { } duration) builder.WithDuration(TimeSpan.FromMinutes(duration));
        if (slidingMinutes is { } sliding) builder.WithSlidingExpiration(TimeSpan.FromMinutes(sliding));

        var build = () => builder.Build();
        build.Should().Throw<InvalidOperationException>().WithMessage("RefreshAhead*must be shorter than*");
    }

    [Fact]
    public void Build_RefreshAheadShorterThanTheDuration_Builds()
        => new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromMinutes(10)).RefreshAhead(TimeSpan.FromMinutes(1)).Build()
            .RefreshAhead.Should().Be(TimeSpan.FromMinutes(1));

    [Fact]
    public void WithDuration_Zero_MessageDoesNotPromiseNoExpiration()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CacheEntryOptions.Builder().WithDuration(TimeSpan.Zero));
        ex.Message.Should().NotContainEquivalentOf("no expiration", "omitting Duration gives DefaultExpiration and MaxValue is capped by MaxExpiration");
    }

    // ---------- Default ICacheManager.TryGetFast treats a cached default(T) as a miss ----------

    [Fact]
    public void TryGetFast_DefaultImplementation_ReportsACachedZeroAsFound()
    {
        ICacheManager manager = new ZeroHoldingManager();

        manager.TryGetFast<int>("present", out var value).Should().BeTrue("0 is cached under \"present\"");
        value.Should().Be(0);
        manager.TryGetFast<int>("absent", out _).Should().BeFalse();
    }

    /// <summary>
    /// A third-party manager that holds the value 0 under "present" and leaves TryGetFast to the interface. Its
    /// TryGetFastAsync can only answer default(T) either way; LookupAsync tells the two apart.
    /// </summary>
    private sealed class ZeroHoldingManager : ICacheManager
    {
        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => throw new NotSupportedException();

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => throw new NotSupportedException();

        public ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => new(keyGenerator.GenerateKey(methodName, args, policy) == "present" ? CacheLookupResult<T>.Hit(default!) : CacheLookupResult<T>.Miss);

        public ValueTask<T?> TryGetFastAsync<T>(string cacheKey) => new(default(T));

        public Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task InvalidateByTagsAsync(params string[] tags) => Task.CompletedTask;

        public Task InvalidateByKeysAsync(params string[] keys) => Task.CompletedTask;

        public Task InvalidateByTagPatternAsync(string pattern) => Task.CompletedTask;
    }
}
