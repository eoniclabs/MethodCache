using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// <c>DurationFor</c> gives each result its own duration, such as the time left on a token, in place of the method's
/// <c>Duration</c>.
/// </summary>
public class DurationForTests
{
    private static readonly string GetId = MethodIds.For(typeof(ITokens), nameof(ITokens.GetAsync));
    private static readonly string CountId = MethodIds.For(typeof(ITokens), nameof(ITokens.CountAsync));

    public sealed record Token(string Value, DateTimeOffset ExpiresAt);

    public interface ITokens
    {
        [Cache(Duration = "1h")] Task<Token> GetAsync(string audience);
        [Cache] Task<int> CountAsync(int id);
    }

    public sealed class Tokens(TimeProvider time) : ITokens
    {
        public int Calls { get; private set; }
        public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(10);

        public Task<Token> GetAsync(string audience) => Task.FromResult(new Token("t" + ++Calls, time.GetUtcNow() + Lifetime));
        public Task<int> CountAsync(int id) => Task.FromResult(++Calls);
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task DurationFor_KeepsEachResultForTheDurationItGives_InPlaceOfTheAttributes()
    {
        var (provider, service) = Build(mc => mc.Method((ITokens t) => t.GetAsync(Any<string>.Value))
            .DurationFor(token => token.ExpiresAt - _time.GetUtcNow() - TimeSpan.FromMinutes(1)));
        await using var _ = provider;
        var tokens = provider.GetRequiredService<ITokens>();

        await tokens.GetAsync("api");
        _time.Advance(TimeSpan.FromMinutes(8));
        var cached = await tokens.GetAsync("api");
        _time.Advance(TimeSpan.FromMinutes(2));
        var reloaded = await tokens.GetAsync("api");

        Assert.Equal("t1", cached.Value);
        Assert.Equal("t2", reloaded.Value);
    }

    [Fact]
    public async Task DurationFor_ZeroOrLessOrThrowing_DoesNotCacheTheResult()
    {
        var (provider, service) = Build(mc =>
        {
            mc.Method((ITokens t) => t.GetAsync(Any<string>.Value)).DurationFor(token => token.ExpiresAt - _time.GetUtcNow() - TimeSpan.FromMinutes(1));
            mc.Method((ITokens t) => t.CountAsync(Any<int>.Value)).DurationFor(_ => throw new InvalidOperationException("no duration"));
        });
        await using var _ = provider;
        var tokens = provider.GetRequiredService<ITokens>();
        service.Lifetime = TimeSpan.FromSeconds(30);

        await tokens.GetAsync("api");
        await tokens.GetAsync("api");
        await tokens.CountAsync(1);
        await tokens.CountAsync(1);

        Assert.Equal(4, service.Calls);
    }

    [Fact]
    public async Task DurationInAppSettings_ReplacesDurationForInCode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"MethodCache:Methods:{GetId}:Duration"] = "10m" }).Build();
        var (provider, service) = Build(mc =>
        {
            mc.Method((ITokens t) => t.GetAsync(Any<string>.Value)).DurationFor(_ => TimeSpan.FromSeconds(1));
            mc.FromConfiguration(configuration);
        });
        await using var _ = provider;
        var tokens = provider.GetRequiredService<ITokens>();

        await tokens.GetAsync("api");
        _time.Advance(TimeSpan.FromMinutes(5));
        await tokens.GetAsync("api");

        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public void DurationFor_ForAnotherResultType_FailsStartup()
    {
        var (provider, _) = Build(mc => mc.Method((ITokens t) => t.CountAsync(0)).Configure(o => o.DurationFor<string>(_ => TimeSpan.FromMinutes(1))));
        using var __ = provider;

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains("DurationFor<String> can't check the Int32 result", error.Message, StringComparison.Ordinal);
        Assert.Contains(CountId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DurationFor_InDefaults_Throws()
    {
        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            var (provider, _) = Build(mc => mc.Defaults(o => o.DurationFor<int>(_ => TimeSpan.FromMinutes(1))));
            using var __ = provider;
            provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies();
        });

        Assert.Contains("DurationFor is set in", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_DurationAndDurationFor_TheLastOneWins()
    {
        var withDurationLast = new CacheEntryOptions.Builder().DurationFor<string>(_ => TimeSpan.FromSeconds(5)).WithDuration(TimeSpan.FromMinutes(2)).Build();
        var withDurationForLast = new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromMinutes(2)).DurationFor<string>(text => TimeSpan.FromSeconds(text.Length)).Build();

        Assert.Equal(TimeSpan.FromMinutes(2), withDurationLast.DurationOf("abc"));
        Assert.Null(withDurationForLast.Duration);
        Assert.Equal(TimeSpan.FromSeconds(3), withDurationForLast.DurationOf("abc"));
        Assert.Equal(TimeSpan.Zero, withDurationForLast.DurationOf(""));
        Assert.False(withDurationForLast.CanCache(""));
        Assert.True(withDurationForLast.CanCache("abc"));
        Assert.Throws<InvalidOperationException>(() => withDurationForLast.DurationOf(42));
    }

    [Fact]
    public async Task GetOrCreateAsync_UsesTheDurationTheResultGives()
    {
        await using var provider = new ServiceCollection().AddSingleton<TimeProvider>(_time).AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var calls = 0;
        ValueTask<string> Load(CancellationToken _) => ValueTask.FromResult(++calls == 1 ? "short" : "a longer value");

        await cache.GetOrCreateAsync("key", Load, o => o.DurationFor<string>(text => TimeSpan.FromSeconds(text.Length)));
        _time.Advance(TimeSpan.FromSeconds(6));
        await cache.GetOrCreateAsync("key", Load, o => o.DurationFor<string>(text => TimeSpan.FromSeconds(text.Length)));
        _time.Advance(TimeSpan.FromSeconds(6));
        var stillCached = await cache.GetOrCreateAsync("key", Load, o => o.DurationFor<string>(text => TimeSpan.FromSeconds(text.Length)));

        Assert.Equal(2, calls);
        Assert.Equal("a longer value", stillCached);
    }

    private (ServiceProvider Provider, Tokens Service) Build(Action<MethodCacheBuilder> configure)
    {
        var service = new Tokens(_time);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<ITokens>(service);
        services.AddMethodCache(configure);
        return (services.BuildServiceProvider(), service);
    }
}
