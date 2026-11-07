using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// <c>SlidingFor</c> gives each result its own sliding window in place of the method's <c>SlidingExpiration</c>, so a
/// result that loaded can stay while it is read and one that failed lives a fixed time.
/// </summary>
public class SlidingForTests
{
    private static readonly string GetId = MethodIds.For(typeof(IPreviews), nameof(IPreviews.GetAsync));
    private static readonly string CountId = MethodIds.For(typeof(IPreviews), nameof(IPreviews.CountAsync));

    public sealed record Preview(bool Succeeded, int Load);

    public interface IPreviews
    {
        [Cache] Task<Preview> GetAsync(string url);
        [Cache(Duration = "1h")] Task<int> CountAsync(int id);
    }

    public sealed class Previews : IPreviews
    {
        public int Calls { get; private set; }
        public bool Succeeds { get; set; } = true;

        public Task<Preview> GetAsync(string url) => Task.FromResult(new Preview(Succeeds, ++Calls));
        public Task<int> CountAsync(int id) => Task.FromResult(++Calls);
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private static void SuccessSlidesFailureStaysTwoHours(MethodCacheBuilder mc)
        => mc.Method((IPreviews p) => p.GetAsync(Any<string>.Value))
            .DurationFor(preview => preview.Succeeded ? TimeSpan.FromDays(1) : TimeSpan.FromHours(2))
            .SlidingFor(preview => preview.Succeeded ? TimeSpan.FromMinutes(30) : TimeSpan.Zero);

    [Fact]
    public async Task SlidingFor_AResultWithAWindow_StaysWhileItIsRead_AndExpiresWhenItIsNot()
    {
        var (provider, service) = Build(SuccessSlidesFailureStaysTwoHours);
        await using var _ = provider;
        var previews = provider.GetRequiredService<IPreviews>();

        await previews.GetAsync("a");
        for (var read = 0; read < 4; read++)
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(1, (await previews.GetAsync("a")).Load);
        }

        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(2, (await previews.GetAsync("a")).Load);
    }

    [Fact]
    public async Task SlidingFor_Zero_TurnsSlidingOffForThatResult_SoItLivesItsDuration()
    {
        var (provider, service) = Build(SuccessSlidesFailureStaysTwoHours);
        await using var _ = provider;
        var previews = provider.GetRequiredService<IPreviews>();
        service.Succeeds = false;

        await previews.GetAsync("a");
        _time.Advance(TimeSpan.FromMinutes(90));
        var unread = await previews.GetAsync("a");
        _time.Advance(TimeSpan.FromMinutes(31));
        var expired = await previews.GetAsync("a");

        Assert.Equal(1, unread.Load);
        Assert.Equal(2, expired.Load);
    }

    [Fact]
    public async Task SlidingFor_Throwing_DoesNotCacheTheResult()
    {
        var (provider, service) = Build(mc => mc.Method((IPreviews p) => p.CountAsync(Any<int>.Value)).SlidingFor(_ => throw new InvalidOperationException("no window")));
        await using var _ = provider;
        var previews = provider.GetRequiredService<IPreviews>();

        await previews.CountAsync(1);
        await previews.CountAsync(1);

        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task SlidingExpirationInAppSettings_ReplacesSlidingForInCode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"MethodCache:Methods:{CountId}:SlidingExpiration"] = "off" }).Build();
        var (provider, service) = Build(mc =>
        {
            mc.Method((IPreviews p) => p.CountAsync(Any<int>.Value)).SlidingFor(_ => TimeSpan.FromMinutes(1));
            mc.FromConfiguration(configuration);
        });
        await using var _ = provider;
        var previews = provider.GetRequiredService<IPreviews>();

        await previews.CountAsync(1);
        _time.Advance(TimeSpan.FromMinutes(5));
        await previews.CountAsync(1);

        Assert.Equal(1, service.Calls);
        var sources = provider.GetRequiredService<IPolicyRegistry>().GetPolicy(CountId);
        Assert.Null(sources.Policy.SlidingFor);
    }

    [Fact]
    public void SlidingFor_ForAnotherResultType_FailsStartup()
    {
        var (provider, _) = Build(mc => mc.Method((IPreviews p) => p.CountAsync(0)).Configure(o => o.SlidingFor<string>(_ => TimeSpan.FromMinutes(1))));
        using var __ = provider;

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains("SlidingFor<string> can't check the int result", error.Message, StringComparison.Ordinal);
        Assert.Contains(CountId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SlidingFor_InDefaults_Throws()
    {
        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            var (provider, _) = Build(mc => mc.Defaults(o => o.SlidingFor<int>(_ => TimeSpan.FromMinutes(1))));
            using var __ = provider;
            provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies();
        });

        Assert.Contains("SlidingFor is set in", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_SlidingExpirationAndSlidingFor_TheLastOneWins()
    {
        var withSlidingLast = new CacheEntryOptions.Builder().SlidingFor<string>(_ => TimeSpan.FromSeconds(5)).WithSlidingExpiration(TimeSpan.FromMinutes(2)).Build();
        var withSlidingForLast = new CacheEntryOptions.Builder().WithSlidingExpiration(TimeSpan.FromMinutes(2))
            .SlidingFor<string>(text => text == "throw" ? throw new InvalidOperationException() : TimeSpan.FromSeconds(text.Length - 1)).Build();

        Assert.Equal(TimeSpan.FromMinutes(2), withSlidingLast.SlidingExpirationOf("abc"));
        Assert.Null(withSlidingForLast.SlidingExpiration);
        Assert.Equal(TimeSpan.FromSeconds(2), withSlidingForLast.SlidingExpirationOf("abc"));
        Assert.Equal(TimeSpan.Zero, withSlidingForLast.SlidingExpirationOf("a"));
        Assert.True(withSlidingForLast.CanCache("a"));
        Assert.False(withSlidingForLast.CanCache("throw"));
        Assert.Throws<InvalidOperationException>(() => withSlidingForLast.SlidingExpirationOf(42));
    }

    [Fact]
    public async Task GetOrCreateAsync_UsesTheSlidingWindowTheResultGives()
    {
        await using var provider = new ServiceCollection().AddSingleton<TimeProvider>(_time).AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var calls = 0;
        ValueTask<string> Load(CancellationToken _) => ValueTask.FromResult(++calls == 1 ? "slides" : "fixed");
        Action<CacheEntryOptions.Builder> options = o => o.WithDuration(TimeSpan.FromMinutes(10))
            .SlidingFor<string>(text => text == "slides" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero);

        await cache.GetOrCreateAsync("slides", Load, options);
        _time.Advance(TimeSpan.FromSeconds(50));
        await cache.GetOrCreateAsync("slides", Load, options);
        _time.Advance(TimeSpan.FromSeconds(50));
        var slid = await cache.GetOrCreateAsync("slides", Load, options);
        _time.Advance(TimeSpan.FromSeconds(70));
        var expired = await cache.GetOrCreateAsync("slides", Load, options);
        _time.Advance(TimeSpan.FromMinutes(5));
        var unread = await cache.GetOrCreateAsync("slides", Load, options);

        Assert.Equal("slides", slid);
        Assert.Equal("fixed", expired);
        Assert.Equal("fixed", unread);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AValueReadFromTheDistributedCache_GetsTheSlidingWindowItGives(bool many)
    {
        var shared = new MethodCache.TestSupport.AtomicTestCache(_time);
        var writer = Node(shared);
        var reader = Node(shared);
        Action<CacheEntryOptions.Builder> options = o => o.WithDuration(TimeSpan.FromMinutes(10))
            .SlidingFor<string>(text => text == "slides" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero);
        async Task<string> Read(ICacheManager node, string key, string value) => many
            ? (await node.GetOrCreateManyAsync<string>([key], (keys, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(keys.ToDictionary(k => k, _ => value)), options))[key]
            : await node.GetOrCreateAsync(key, _ => ValueTask.FromResult(value), options);

        Assert.Equal("slides", await Read(writer, "s", "slides"));
        Assert.Equal("fixed", await Read(writer, "f", "fixed"));
        Assert.Equal("slides", await Read(reader, "s", "reader"));
        Assert.Equal("fixed", await Read(reader, "f", "reader"));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        // Gone from the distributed cache; without a backplane the reader keeps its copy until it expires
        await writer.InvalidateByKeysAsync("s", "f");

        _time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal("slides", await Read(reader, "s", "reader"));
        Assert.Equal("fixed", await Read(reader, "f", "reader"));
        _time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal("slides", await Read(reader, "s", "reader"));
        _time.Advance(TimeSpan.FromSeconds(70));
        Assert.Equal("reader", await Read(reader, "s", "reader"));
        Assert.Equal("fixed", await Read(reader, "f", "reader"));
    }

    private ICacheManager Node(MethodCache.TestSupport.AtomicTestCache shared)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => shared, options => options.ClockSkewTolerance = TimeSpan.Zero);
        return services.BuildServiceProvider().GetRequiredService<ICacheManager>();
    }

    private (ServiceProvider Provider, Previews Service) Build(Action<MethodCacheBuilder> configure)
    {
        var service = new Previews();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IPreviews>(service);
        services.AddMethodCache(configure);
        return (services.BuildServiceProvider(), service);
    }
}
