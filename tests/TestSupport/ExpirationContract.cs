using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static MethodCache.TestSupport.PolicyBackendContract;

namespace MethodCache.TestSupport;

/// <summary>
/// Every expiration rule, seen from two instances that share one distributed cache: how long the copy another instance
/// reads lives, when it loads the value again, and what each instance serves when a load fails. Run unchanged against
/// every distributed cache MethodCache ships.
/// </summary>
public static class ExpirationContract
{
    public static async Task RunAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create, FakeTimeProvider time)
    {
        // Both instances use a fresh scope, and neither listens to the backplane: these assertions are about what the
        // distributed cache stores and for how long, not about messages that make an instance drop its copy early.
        var scope = "expiry:" + Guid.NewGuid().ToString("N") + ":";
        Action<MethodCacheBuilder> isolate = builder => builder.Services.PostConfigure<DistributedCacheOptions>(o => o.KeyPrefix += scope);
        await using var first = await create(new Origin(700), isolate);
        await using var second = await create(new Origin(800), isolate);
        await DetachAsync(first);
        await DetachAsync(second);
        var writer = first.GetRequiredService<ICacheManager>();
        var reader = second.GetRequiredService<ICacheManager>();
        var loader = first.GetRequiredService<IOrigin>();
        var other = second.GetRequiredService<IOrigin>();

        // A fixed Duration: the copy another instance reads ends with the stored one, and the next load is shared again.
        var loaded = await loader.FixedAsync(1);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(loaded, await other.FixedAsync(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var reloaded = await other.FixedAsync(1);
        Assert.NotEqual(loaded, reloaded);
        Assert.Equal(reloaded, await loader.FixedAsync(1));
        var fixedFor = Options(o => o.WithDuration(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await writer.GetOrCreateAsync("fixed", _ => ValueTask.FromResult(1), fixedFor));
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, await reader.GetOrCreateAsync("fixed", _ => ValueTask.FromResult(2), fixedFor));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await reader.GetOrCreateAsync("fixed", _ => ValueTask.FromResult(2), fixedFor));
        Assert.Equal(2, await writer.GetOrCreateAsync("fixed", _ => ValueTask.FromResult(3), fixedFor));

        // The Duration counts from when the load started, so a load that took longer than it isn't shared at all, and
        // one that took part of it is stored for the rest.
        var slow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = writer.GetOrCreateAsync("slow", _ => { started.TrySetResult(); return new ValueTask<int>(slow.Task); }, o => o.WithDuration(TimeSpan.FromSeconds(10))).AsTask();
        await started.Task;
        time.Advance(TimeSpan.FromSeconds(20));
        slow.SetResult(10); Assert.Equal(10, await loading);
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(10, await reader.GetOrCreateAsync("slow", _ => ValueTask.FromResult(20), o => o.WithDuration(TimeSpan.FromSeconds(30))));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(20, await reader.GetOrCreateAsync("slow", _ => ValueTask.FromResult(20), o => o.WithDuration(TimeSpan.FromSeconds(30))));

        // DurationFor: the time the result gives sets how long the stored copy lives, and a result it gives no time is
        // neither kept nor shared.
        var durationFor = Options(o => o.DurationFor<int>(seconds => TimeSpan.FromSeconds(seconds)));
        Assert.Equal(6, await writer.GetOrCreateAsync("duration-for", _ => ValueTask.FromResult(6), durationFor));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(6, await reader.GetOrCreateAsync("duration-for", _ => ValueTask.FromResult(8), durationFor));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(8, await reader.GetOrCreateAsync("duration-for", _ => ValueTask.FromResult(8), durationFor));
        Assert.Equal(0, await writer.GetOrCreateAsync("duration-for-none", _ => ValueTask.FromResult(0), durationFor));
        Assert.Equal(3, await reader.GetOrCreateAsync("duration-for-none", _ => ValueTask.FromResult(3), durationFor));

        // A sliding window caps the stored copy, since reads on one instance don't reach the others: after a window
        // without reads the other instances load the value again, while the instance that loaded it keeps sliding its
        // own copy in memory. Same for an attribute and a manual call.
        var slid = await loader.SlidingAsync(1);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(slid, await other.SlidingAsync(1));
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(slid, await loader.SlidingAsync(1));
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(slid, await loader.SlidingAsync(1));
        Assert.NotEqual(slid, await other.SlidingAsync(1));
        await writer.SetAsync("sliding-set", 1, o => o.WithDuration(TimeSpan.FromSeconds(5))
            .WithSlidingExpiration(TimeSpan.FromSeconds(1)).WithDistributed(true));
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True((await reader.TryGetAsync<int>("sliding-set")).Found);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False((await reader.TryGetAsync<int>("sliding-set")).Found);

        // The copy another instance reads never slides past the stored one, also when its own policy is longer.
        await writer.GetOrCreateAsync("deadline", _ => ValueTask.FromResult(1), o => o.WithDuration(TimeSpan.FromSeconds(10)));
        var longer = Options(o => o.WithDuration(TimeSpan.FromSeconds(30)).WithSlidingExpiration(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await reader.GetOrCreateAsync("deadline", _ => ValueTask.FromResult(2), longer));
        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1500));
            Assert.Equal(1, await reader.GetOrCreateAsync("deadline", _ => ValueTask.FromResult(2), longer));
        }
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await reader.GetOrCreateAsync("deadline", _ => ValueTask.FromResult(2), longer));

        // SlidingFor: the window the result gives caps the stored copy like a fixed window, and a result it gives no
        // window is stored for the whole Duration.
        var slidingFor = Options(o => o.WithDuration(TimeSpan.FromSeconds(30)).SlidingFor<int>(seconds => TimeSpan.FromSeconds(seconds)));
        Assert.Equal(2, await writer.GetOrCreateAsync("sliding-for", _ => ValueTask.FromResult(2), slidingFor));
        time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(2, await reader.GetOrCreateAsync("sliding-for", _ => ValueTask.FromResult(5), slidingFor));
        Assert.Equal(2, await writer.GetOrCreateAsync("sliding-for", _ => ValueTask.FromResult(9), slidingFor));
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, await writer.GetOrCreateAsync("sliding-for", _ => ValueTask.FromResult(9), slidingFor));
        Assert.Equal(5, await reader.GetOrCreateAsync("sliding-for", _ => ValueTask.FromResult(5), slidingFor));
        Assert.Equal(0, await writer.GetOrCreateAsync("sliding-for-none", _ => ValueTask.FromResult(0), slidingFor));
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await reader.GetOrCreateAsync("sliding-for-none", _ => ValueTask.FromResult(7), slidingFor));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(7, await reader.GetOrCreateAsync("sliding-for-none", _ => ValueTask.FromResult(7), slidingFor));

        // Refresh-ahead: a read in the window reloads the value in the background and replaces the stored copy, so an
        // instance whose own copy ends reads the new value instead of loading it.
        var refresh = Options(o => o.WithDuration(TimeSpan.FromSeconds(10)).WithRefreshAhead(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await writer.GetOrCreateAsync("refresh", _ => ValueTask.FromResult(1), refresh));
        Assert.Equal(1, await reader.GetOrCreateAsync("refresh", _ => ValueTask.FromResult(9), refresh));
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, await writer.GetOrCreateAsync("refresh", _ => ValueTask.FromResult(2), refresh));
        await EventuallyAsync(() => !((InMemoryCacheManager)writer).IsLoading("refresh"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await reader.GetOrCreateAsync("refresh", _ => ValueTask.FromResult(9), refresh));

        // Fail-safe: when a load fails after the entry ended, each instance that has the value in memory serves it, the
        // one that loaded it and the one that read it alike. An instance that never had it has nothing to serve.
        var failSafe = Options(o => o.WithDuration(TimeSpan.FromSeconds(10)).WithFailSafe(TimeSpan.FromMinutes(1)));
        Assert.Equal(1, await writer.GetOrCreateAsync("fail-safe", _ => ValueTask.FromResult(1), failSafe));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await reader.GetOrCreateAsync("fail-safe", _ => ValueTask.FromResult(9), failSafe));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await writer.GetOrCreateAsync("fail-safe", _ => Fails(), failSafe));
        Assert.Equal(1, await reader.GetOrCreateAsync("fail-safe", _ => Fails(), failSafe));
        await using var third = await create(new Origin(900), isolate);
        await DetachAsync(third);
        await Assert.ThrowsAsync<InvalidOperationException>(() => third.GetRequiredService<ICacheManager>()
            .GetOrCreateAsync("fail-safe", _ => Fails(), failSafe).AsTask());
    }

    private static CacheEntryOptions Options(Func<CacheEntryOptions.Builder, CacheEntryOptions.Builder> configure)
        => configure(new CacheEntryOptions.Builder()).Build();

    private static ValueTask<int> Fails() => ValueTask.FromException<int>(new InvalidOperationException("The source is down."));

    private static async Task DetachAsync(ServiceProvider node)
    {
        var backplane = node.GetRequiredKeyedService<IBackplane>("MethodCache");
        await backplane.UnsubscribeAsync();
        Assert.NotEqual(BackplaneState.Connected, backplane.ConnectionState);
    }
}
