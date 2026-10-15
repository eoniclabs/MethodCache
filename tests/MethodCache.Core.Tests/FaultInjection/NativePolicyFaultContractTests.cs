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

    private sealed class ModelBackplane : IBackplane
    {
        public string InstanceId => "model";
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
