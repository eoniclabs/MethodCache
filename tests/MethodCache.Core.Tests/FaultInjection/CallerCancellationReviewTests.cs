using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class CallerCancellationReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCancellation_StopsWaitingForAnUncooperativeFactory(bool failSafe)
    {
        var time = new FakeTimeProvider();
        using var manager = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time);
        var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1) };
        if (failSafe)
        {
            policy = policy with { FailSafe = TimeSpan.FromHours(1), FailSafeTimeout = TimeSpan.FromMinutes(10) };
            await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(1), policy);
            time.Advance(TimeSpan.FromMinutes(2));
        }
        var factory = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var load = manager.GetOrCreateFastAsync("key", "M", () => factory.Task, policy, cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            factory.TrySetResult(2);
        }
        Assert.Equal(2, await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(3), policy));
    }

    [Fact]
    public async Task BulkCancellation_StopsWaitingForABlockedL2Read()
    {
        var time = new FakeTimeProvider();
        var backend = new ChaosDistributedCache(time: time).Inject(ChaosOperations.Get, ChaosFault.HangUntilCancelled());
        using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions { OperationTimeout = TimeSpan.FromHours(1) }, timeProvider: time);
        using var manager = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time, layer);
        using var cancellation = new CancellationTokenSource();
        var load = manager.GetOrCreateManyAsync<int>(["key"], (_, _) => throw new InvalidOperationException("The cancelled read must not run a factory."), cancellationToken: cancellation.Token).AsTask();
        await backend.FirstBlockStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, backend.Blocked);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(10)));
            await backend.FirstBlockCompleted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, backend.Blocked);
            Assert.True(layer.IsAvailable);
        }
        finally
        {
            time.Advance(TimeSpan.FromHours(1));
        }
    }

    [Fact]
    public async Task OwnerCancellation_StopsWaitingForABlockedL2Read()
    {
        var time = new FakeTimeProvider();
        var backend = new ChaosDistributedCache(time: time).Inject(ChaosOperations.Get, ChaosFault.HangUntilCancelled());
        using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions { OperationTimeout = TimeSpan.FromHours(1) }, timeProvider: time);
        using var manager = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time, layer);
        using var cancellation = new CancellationTokenSource();
        var load = manager.GetOrCreateFastAsync("key", "M", () =>
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return Task.FromResult("loaded");
        }, CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1) }, cancellation.Token);
        Assert.Equal(1, backend.Blocked);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(10)));
            await backend.FirstBlockCompleted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, backend.Blocked);
            Assert.True(layer.IsAvailable);
        }
        finally
        {
            time.Advance(TimeSpan.FromHours(1));
            try { await load.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadCancellation_StopsDataAndInvalidationReadsWithoutTrippingBreaker(bool invalidationRead)
    {
        var time = new FakeTimeProvider();
        var backend = new ChaosDistributedCache(time: time);
        using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions { OperationTimeout = TimeSpan.FromHours(1) }, timeProvider: time);
        await layer.WriteAsync("key", 42, ["tag"], time.GetUtcNow().ToUnixTimeMilliseconds(), TimeSpan.FromMinutes(1));
        backend.Inject(ChaosOperations.Get, ChaosFault.HangUntilCancelled() with
        {
            // Metadata reads run in parallel; inject one blocked operation so its completion signal
            // proves that exact operation released its resources, independently of its siblings.
            Times = 1,
            Keys = key => invalidationRead ? !key.StartsWith("methodcache:e6:s1:", StringComparison.Ordinal) : key.StartsWith("methodcache:e6:s1:", StringComparison.Ordinal)
        });
        using var cancellation = new CancellationTokenSource();
        var read = layer.TryGetAsync<int>("key", cancellation.Token).AsTask();
        await backend.FirstBlockStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        await backend.FirstBlockCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, backend.Blocked);
        Assert.True(layer.IsAvailable);
        backend.Heal();
        Assert.True((await layer.TryGetAsync<int>("key")).Found);
    }

    [Fact]
    public async Task ProbeCancellation_AllowsAnotherProbeToRecover()
    {
        var time = new FakeTimeProvider();
        var backend = new ChaosDistributedCache(time: time).Inject(ChaosOperations.Get, ChaosFault.Throw());
        using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions { OperationTimeout = TimeSpan.FromHours(1), CircuitBreakerDuration = TimeSpan.FromSeconds(10) }, timeProvider: time);
        Assert.False((await layer.TryGetAsync<int>("key")).Found);
        Assert.False(layer.IsAvailable);
        time.Advance(TimeSpan.FromSeconds(10));
        backend.Inject(ChaosOperations.Get, ChaosFault.HangUntilCancelled());
        using var cancellation = new CancellationTokenSource();
        var probe = layer.TryGetAsync<int>("key", cancellation.Token).AsTask();
        await backend.FirstBlockStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(10)));
        await backend.FirstBlockCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        backend.Heal();
        Assert.False((await layer.TryGetAsync<int>("key")).Found);
        Assert.True(layer.IsAvailable);
    }

    [Fact]
    public async Task ReadCancellation_StopsWaitingWhenBackendIgnoresToken()
    {
        var time = new FakeTimeProvider();
        var backend = new ChaosDistributedCache(time: time).Inject(ChaosOperations.Get, ChaosFault.HangIgnoringToken());
        using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions { OperationTimeout = TimeSpan.FromHours(1) }, timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        var read = layer.TryGetAsync<int>("key", cancellation.Token).AsTask();
        await backend.FirstBlockStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(layer.IsAvailable);
        }
        finally
        {
            backend.ReleaseHeld();
            await backend.FirstBlockCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

}
