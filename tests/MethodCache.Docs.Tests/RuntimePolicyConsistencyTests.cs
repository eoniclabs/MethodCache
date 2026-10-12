using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class RuntimePolicyConsistencyTests
{
    [Fact]
    public async Task ConcurrentOverrides_LeaveStoredAndEffectivePoliciesInAgreement()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        var resolver = provider.GetRequiredService<IPolicyRegistry>();
        const string method = "Concurrent.Read";
        resolver.GetPolicy(method);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Enumerable.Range(1, 32).Select(seconds => Task.Run(async () =>
        {
            await start.Task;
            await runtime.UpsertAsync(method, builder => builder.WithDuration(TimeSpan.FromSeconds(seconds)));
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(updates);
        var stored = await runtime.GetOverrideAsync(method);
        Assert.Equal(stored!.Policy.Duration, resolver.GetPolicy(method).Policy.Duration);
    }
}
