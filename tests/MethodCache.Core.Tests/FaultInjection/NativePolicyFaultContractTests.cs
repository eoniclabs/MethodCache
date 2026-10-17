using MethodCache.Extensibility;
using MethodCache.TestSupport;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class NativePolicyFaultContractTests
{
    [Theory]
    [InlineData("partial")]
    [InlineData("cancellation")]
    [InlineData("validation")]
    public Task InvalidationFaults_ConformToSharedBackendContract(string scenario)
        => NativePolicyFaultContract.RunAsync(scenario, new AtomicTestCache(), new ModelBackplane());

    [Fact]
    public async Task AssertionFailureWhileStampIsHeld_DrainsCleanupAndPreservesTheFailure()
    {
        var failure = new ControlledAssertionException();
        var run = NativePolicyFaultContract.RunAsync("cancellation", new AtomicTestCache(), new ModelBackplane(),
            afterHold: () => throw failure);
        var actual = await Assert.ThrowsAsync<ControlledAssertionException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, actual);
    }

    [Fact]
    public Task CancellationWithAnEarlierConfirmedStamp_TracksOnlyTheHeldOperation()
        => NativePolicyFaultContract.RunAsync("cancellation", new AtomicTestCache(), new ModelBackplane(), includeEarlierStamp: true)
            .WaitAsync(TimeSpan.FromSeconds(2));

    private sealed class ControlledAssertionException : Exception;

    private sealed class ModelBackplane : IBackplane
    {
        public string InstanceId => "model";
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
