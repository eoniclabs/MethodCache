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
        services.AddSingleton<IConcurrentReader, ConcurrentReader>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var method = typeof(IConcurrentReader).FullName!.Replace('+', '.') + ".Read";
        provider.GetRequiredService<IConcurrentReader>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Enumerable.Range(1, 32).Select(seconds => Task.Run(async () =>
        {
            await start.Task;
            await runtime.UpsertAsync(method, builder => builder.WithDuration(TimeSpan.FromSeconds(seconds)));
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(updates);
        var stored = (await runtime.GetOverridesAsync()).Single(m => m.MethodId == method);
        Assert.Equal(stored.Options.Duration, admin.GetMethods().Single(m => m.MethodId == method).Duration);
    }
    public interface IConcurrentReader { [Cache] int Read(); }
    public sealed class ConcurrentReader : IConcurrentReader { public int Read() => 42; }
}
