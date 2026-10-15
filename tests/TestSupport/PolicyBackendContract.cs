using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.TestSupport;

// Shared assertions run unchanged against both real providers. Each factory creates an isolated container.
public static class PolicyBackendContract
{
    private static readonly string MethodId = typeof(IOrigin).FullName!.Replace('+', '.') + ".ReadAsync";

    public static async Task RunAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create, FakeTimeProvider time)
    {
        await using var first = await create(new Origin(100), null);
        await using var second = await create(new Origin(200), null);
        var a = first.GetRequiredService<IOrigin>();
        var b = second.GetRequiredService<IOrigin>();
        Assert.Equal(101, await a.ReadAsync(1));
        Assert.Equal(101, await b.ReadAsync(1));
        var receiverHandle = PolicyHandle.For(second.GetRequiredService<IPolicyRegistry>(), MethodId);
        var before = receiverHandle.Current;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverHandle.Subscribe(policy => { if (!ReferenceEquals(policy.Generation, before.Generation)) delivered.TrySetResult(); });
        time.Advance(TimeSpan.FromMilliseconds(1));
        await first.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithDuration(TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromMilliseconds(100));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMinutes(5), receiverHandle.Current.Duration);
        // The typed method message has retired the receiver. The following assertions isolate L2
        // namespaces from the documented possibility of delayed messages evicting fresh local data.
        await first.GetRequiredKeyedService<IBackplane>("MethodCache").UnsubscribeAsync();
        await second.GetRequiredKeyedService<IBackplane>("MethodCache").UnsubscribeAsync();
        Assert.Equal(102, await a.ReadAsync(1));
        Assert.Equal(201, await b.ReadAsync(1));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(103, await a.ReadAsync(1));
        Assert.Equal(201, await b.ReadAsync(1));
        await using var third = await create(new Origin(300), null);
        await third.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithDuration(TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(301, await third.GetRequiredService<IOrigin>().ReadAsync(1));
        Assert.NotEqual(PolicyHandle.For(first.GetRequiredService<IPolicyRegistry>(), MethodId).Current.Identity,
            PolicyHandle.For(third.GetRequiredService<IPolicyRegistry>(), MethodId).Current.Identity);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await first.GetRequiredService<IRuntimeCacheConfigurator>().RemoveAsync(MethodId);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(104, await a.ReadAsync(1));
        await using var cold = await create(new Origin(400), null);
        Assert.Equal(104, await cold.GetRequiredService<IOrigin>().ReadAsync(1));

        var writer = first.GetRequiredService<ICacheManager>();
        var receiver = cold.GetRequiredService<ICacheManager>();
        await writer.GetOrCreateAsync<string?>("null", (_, _) => ValueTask.FromResult<string?>(null));
        var nullHit = await receiver.TryGetAsync<string?>("null");
        Assert.True(nullHit.Found); Assert.Null(nullHit.Value);
        await using var rejectsNull = await create(new Origin(500), builder => builder.Memory(o => o.CacheNullValues = false));
        Assert.Equal("origin", await rejectsNull.GetRequiredService<ICacheManager>().GetOrCreateAsync("null", (_, _) => ValueTask.FromResult("origin")));
        await writer.GetOrCreateAsync("predicate", (_, _) => ValueTask.FromResult(1));
        Assert.Equal(2, await receiver.GetOrCreateAsync("predicate", (_, _) => ValueTask.FromResult(2), o => o.CacheWhen<int>(value => value > 1)));
        await writer.GetOrCreateAsync("local-only", (_, _) => ValueTask.FromResult(1));
        Assert.Equal(2, await receiver.GetOrCreateAsync("local-only", (_, _) => ValueTask.FromResult(2), o => o.WithDistributed(false)));
        await using var verifier = await create(new Origin(600), null);
        Assert.Equal(1, await verifier.GetRequiredService<ICacheManager>().GetOrCreateAsync("local-only", (_, _) => ValueTask.FromResult(3)));

        // Deadline assertions have no dependency on the preceding policy mutations or their callbacks.
        // Keep both timed nodes on one fresh physical/backplane scope, with subscriptions ready in Create.
        var expiryScope = "expiry:" + Guid.NewGuid().ToString("N") + ":";
        Action<MethodCacheBuilder> isolateExpiry = builder => builder.Services.PostConfigure<DistributedCacheOptions>(o => o.KeyPrefix += expiryScope);
        await using var timedFirst = await create(new Origin(700), isolateExpiry);
        await using var timedSecond = await create(new Origin(800), isolateExpiry);
        var timedWriter = timedFirst.GetRequiredService<ICacheManager>();
        var timedReceiver = timedSecond.GetRequiredService<ICacheManager>();
        var slow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = timedWriter.GetOrCreateAsync("slow", (_, _) => { started.TrySetResult(); return new ValueTask<int>(slow.Task); }, o => o.WithDuration(TimeSpan.FromSeconds(10))).AsTask();
        await started.Task;
        time.Advance(TimeSpan.FromSeconds(20));
        slow.SetResult(10); Assert.Equal(10, await loading);
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(10, await timedReceiver.GetOrCreateAsync("slow", (_, _) => ValueTask.FromResult(20), o => o.WithDuration(TimeSpan.FromSeconds(30))));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(20, await timedReceiver.GetOrCreateAsync("slow", (_, _) => ValueTask.FromResult(20), o => o.WithDuration(TimeSpan.FromSeconds(30))));
        await timedWriter.GetOrCreateAsync("deadline", (_, _) => ValueTask.FromResult(1), o => o.WithDuration(TimeSpan.FromSeconds(10)));
        var sliding = new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromSeconds(30))
            .WithSlidingExpiration(TimeSpan.FromSeconds(2)).FailSafe(TimeSpan.FromSeconds(5)).Build();
        Assert.Equal(1, await timedReceiver.GetOrCreateAsync("deadline", (_, _) => ValueTask.FromResult(2), sliding));
        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1500));
            Assert.Equal(1, await timedReceiver.GetOrCreateAsync("deadline", (_, _) => ValueTask.FromResult(2), sliding));
        }
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => timedReceiver.GetOrCreateAsync<int>("deadline",
            (_, _) => ValueTask.FromException<int>(new InvalidOperationException()), sliding).AsTask());

        await writer.GetOrCreateAsync("version", (_, _) => ValueTask.FromResult(7), o => o.WithVersion(2));
        Assert.True((await verifier.GetRequiredService<ICacheManager>().TryGetAsync<int>("version", version: 2)).Found);
        var result = await writer.InvalidateKeysAsync([CacheManagerExtensions.GetCacheKey("version", 2), CacheManagerExtensions.GetCacheKey("version", 2)]);
        Assert.Equal(1, result.Requested); Assert.Equal(1, result.Local); Assert.Equal(1, result.Distributed);
        Assert.Equal(0, result.Queued); Assert.Equal(1, result.Published); Assert.Equal(0, result.PublicationQueued);
    }

    public interface IOrigin { [Cache(Duration = "00:05:00")] Task<int> ReadAsync(int id); }
    public sealed class Origin(int basis) : IOrigin
    {
        private int _calls;
        public Task<int> ReadAsync(int id) => Task.FromResult(basis + Interlocked.Increment(ref _calls));
    }
}
