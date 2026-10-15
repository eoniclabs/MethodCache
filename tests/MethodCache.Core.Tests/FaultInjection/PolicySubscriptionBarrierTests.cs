using MethodCache.Extensibility;
using MethodCache.TestSupport;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class PolicySubscriptionBarrierTests
{
    [Fact]
    public async Task Ready_WaitsForSubscriptionAcknowledgementAndRecoveryCallback()
    {
        var inner = new HeldSubscription();
        await using var barrier = new PolicySubscriptionBarrier(inner);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribing = barrier.SubscribeAsync(async _ => { callbackStarted.TrySetResult(); await finishCallback.Task; });
        try
        {
            await inner.Started.Task;
            Assert.Equal(BackplaneState.Connected, barrier.ConnectionState);
            Assert.False(barrier.Ready.Task.IsCompleted);
            inner.Acknowledge.TrySetResult();
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(barrier.Ready.Task.IsCompleted);
            finishCallback.TrySetResult();
            await subscribing.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(barrier.Ready.Task.IsCompletedSuccessfully);
        }
        finally { inner.Acknowledge.TrySetResult(); finishCallback.TrySetResult(); }
    }

    private sealed class HeldSubscription : IBackplane
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Acknowledge { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string InstanceId => "subscription-barrier-test";
        public BackplaneState ConnectionState => BackplaneState.Connected;
        public async Task SubscribeAsync(Func<BackplaneMessage, Task> callback, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(); await Acknowledge.Task;
            await callback(new BackplaneMessage { Type = BackplaneMessageType.ClearAll });
        }
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
