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
        using var manager = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time);
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(1)));
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
        using var manager = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time, layer);
        using var cancellation = new CancellationTokenSource();
        var load = manager.GetOrCreateManyAsync<int>(["key"], (_, _, _) => throw new InvalidOperationException("The cancelled read must not run a factory."), cancellationToken: cancellation.Token).AsTask();
        async Task WaitForBackendAsync()
        {
            while (backend.Blocked == 0 && !load.IsCompleted) await Task.Yield();
        }
        await WaitForBackendAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, backend.Blocked);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(1)));
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
        using var manager = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time, layer);
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            time.Advance(TimeSpan.FromHours(1));
            try { await load.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }
}
