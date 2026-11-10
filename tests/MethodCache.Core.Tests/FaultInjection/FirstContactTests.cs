using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>
/// A host makes the first contact with the store when it has started. Opening the connection can take longer than one
/// operation: until the store answers, the instance uses its memory cache only, and a slow answer opens no breaker.
/// </summary>
public class FirstContactTests
{
    private static (IAtomicInvalidationStore Store, TaskCompletionSource<TimeSpan> Connecting, Func<int> Contacts) SlowStore()
    {
        var store = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        var connecting = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var contacts = 0;
        // Stops when its token is cancelled, as a real client does
        ((IDistributedEpochStore)store).EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Interlocked.Increment(ref contacts);
                return new ValueTask<TimeSpan>(connecting.Task.WaitAsync(call.Arg<CancellationToken>()));
            });
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(null));
        return (store, connecting, () => Volatile.Read(ref contacts));
    }

    [Fact]
    public async Task SlowFirstContact_OpensNoBreaker_AndTheStoreIsUsedOnceItAnswers()
    {
        var clock = new FakeTimeProvider();
        var (store, connecting, contacts) = SlowStore();
        await using var layer = new DistributedCacheLayer(store, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock);
        var contact = layer.BeginFirstContact();

        // The first caller waits up to the operation timeout, then uses memory only
        var first = layer.TryGetAsync<int>("key").AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await first).Found);
        Assert.True(layer.IsAvailable);

        // Later callers don't wait while the store is still connecting
        var second = layer.TryGetAsync<int>("key");
        Assert.True(second.IsCompleted);
        Assert.False((await second).Found);

        connecting.SetResult(new DistributedCacheOptions().TagRetention);
        await contact;
        Assert.True(await layer.CheckAvailableAsync(default));
        Assert.Equal(1, contacts());
    }

    [Fact]
    public async Task FirstContactThatAnswersWithinTheTimeout_IsWaitedFor()
    {
        var clock = new FakeTimeProvider();
        var (store, connecting, contacts) = SlowStore();
        await using var layer = new DistributedCacheLayer(store, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock);
        _ = layer.BeginFirstContact();

        var check = layer.CheckAvailableAsync(default);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        connecting.SetResult(new DistributedCacheOptions().TagRetention);

        Assert.True(await check);
        Assert.Equal(1, contacts());
    }

    [Fact]
    public async Task FirstContactThatNeverAnswers_OpensTheBreakerAtTheLimit()
    {
        var clock = new FakeTimeProvider();
        var (store, _, contacts) = SlowStore();
        await using var layer = new DistributedCacheLayer(store, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock);
        var contact = layer.BeginFirstContact();
        // The contact runs in the background: its limit counts from when it reached the store
        await WaitUntilAsync(() => contacts() == 1);

        clock.Advance(DistributedCacheLayer.FirstContactLimit - TimeSpan.FromMilliseconds(1));
        Assert.True(layer.IsAvailable);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await contact;
        Assert.False(layer.IsAvailable);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task FirstContactThatFails_OpensTheBreaker()
    {
        var store = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        ((IDistributedEpochStore)store).EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<TimeSpan>(new IOException("refused")));
        await using var layer = new DistributedCacheLayer(store, new JsonCacheSerializer(), new DistributedCacheOptions());

        await layer.BeginFirstContact();

        Assert.False(layer.IsAvailable);
        Assert.False((await layer.TryGetAsync<int>("key")).Found);
    }

    [Fact]
    public async Task HostThatStarted_MakesTheFirstContact()
    {
        var store = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        var contacted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IDistributedEpochStore)store).EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => { contacted.TrySetResult(); return new ValueTask<TimeSpan>(call.Arg<TimeSpan>()); });
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMethodCache();
        builder.Services.AddMethodCacheDistributedCache(_ => store);
        using var host = builder.Build();

        await host.StartAsync();

        await contacted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync();
    }
}
