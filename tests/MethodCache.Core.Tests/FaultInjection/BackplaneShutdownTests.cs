using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.FaultInjection;

public class BackplaneShutdownTests
{
    [Fact]
    public void Dispose_CompletedSubscription_UnsubscribesOnce()
    {
        var backplane = new ControlledBackplane();
        var layer = Create(backplane);
        layer.Attach(_ => { }, _ => { }, () => { }, _ => { });
        layer.Dispose();
        layer.Dispose();
        Assert.Equal(1, backplane.UnsubscribeCalls);
    }

    [Fact]
    public void CacheManagerDisposal_StopsItsAttachedDistributedSubscription()
    {
        var backplane = new ControlledBackplane();
        var layer = Create(backplane);
        var manager = new InMemoryCacheManager(null, new FakeTimeProvider(), layer);
        manager.Dispose();
        Assert.Equal(1, backplane.UnsubscribeCalls);
        layer.Dispose();
        Assert.Equal(1, backplane.UnsubscribeCalls);
    }

    [Fact]
    public async Task Dispose_LateCallback_DoesNotTouchCacheOrMetrics()
    {
        var backplane = new ControlledBackplane();
        var layer = Create(backplane);
        var calls = 0;
        layer.Attach(_ => calls++, _ => calls++, () => calls++, _ => calls++);
        layer.Dispose();
        await backplane.Handler!(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = "key", InstanceId = "remote" });
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Attach_AfterDisposal_CannotStartSubscription()
    {
        var backplane = new ControlledBackplane();
        var layer = Create(backplane);
        layer.Dispose();
        Record.Exception(() => layer.Attach(_ => { }, _ => { }, () => { }, _ => { }));
        Assert.Equal(0, backplane.SubscribeCalls);
    }

    [Fact]
    public async Task DisposeAsync_PendingSubscription_CleansUpAfterItFinishes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backplane = new ControlledBackplane { Subscription = gate.Task };
        var layer = Create(backplane);
        layer.Attach(_ => { }, _ => { }, () => { }, _ => { });
        var shutdown = layer.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        gate.SetResult();
        await shutdown;
        Assert.Equal(1, backplane.UnsubscribeCalls);
        await layer.DisposeAsync();
        Assert.Equal(1, backplane.UnsubscribeCalls);
    }

    [Fact]
    public async Task DisposeAsync_SubscriptionIgnoresCancellation_IsBoundedAndCleansLateRegistration()
    {
        var clock = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backplane = new ControlledBackplane { Subscription = gate.Task };
        var layer = Create(backplane, clock);
        layer.Attach(_ => { }, _ => { }, () => { }, _ => { });
        var shutdown = layer.DisposeAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));
        await shutdown;
        Assert.Equal(1, backplane.UnsubscribeCalls);
        gate.SetResult();
        await backplane.SecondUnsubscribe.Task;
        Assert.Equal(2, backplane.UnsubscribeCalls);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribeIgnoresCancellation_IsBounded()
    {
        var clock = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backplane = new ControlledBackplane { Unsubscription = gate.Task };
        var layer = Create(backplane, clock);
        layer.Attach(_ => { }, _ => { }, () => { }, _ => { });
        var shutdown = layer.DisposeAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));
        await shutdown;
        gate.SetResult();
        Assert.Equal(1, backplane.UnsubscribeCalls);
    }

    private static DistributedCacheLayer Create(ControlledBackplane backplane, FakeTimeProvider? clock = null) => new(
        new MethodCache.TestSupport.AtomicTestCache(),
        new JsonCacheSerializer(), new DistributedCacheOptions { OperationTimeout = TimeSpan.FromSeconds(1) }, backplane, clock ?? new FakeTimeProvider());

    private sealed class ControlledBackplane : IBackplane
    {
        public int SubscribeCalls;
        public int UnsubscribeCalls;
        public Func<BackplaneMessage, Task>? Handler;
        public Task Subscription = Task.CompletedTask;
        public Task Unsubscription = Task.CompletedTask;
        public readonly TaskCompletionSource SecondUnsubscribe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string InstanceId => "local";
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
        {
            SubscribeCalls++;
            Handler = onMessage;
            return Subscription;
        }
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            UnsubscribeCalls++;
            if (UnsubscribeCalls == 2) SecondUnsubscribe.TrySetResult();
            return Unsubscription;
        }
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
