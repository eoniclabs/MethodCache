using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class GeneratedTelemetryContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedSync_CoreTelemetry_RecordsOriginFailureOrOwnedInvalidation(bool distributed)
    {
        var spans = new ConcurrentQueue<Activity>();
        var inner = new GeneratedTelemetryService { Fail = !distributed };
        var services = new ServiceCollection();
        services.AddSingleton<IGeneratedTelemetryService>(inner);
        services.AddMethodCache();
        if (distributed) services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        using var provider = services.BuildServiceProvider();
        using var listener = Listen(provider, spans);
        var service = provider.GetRequiredService<IGeneratedTelemetryService>();
        if (distributed)
        {
            Assert.Equal(1, service.Sync(42));
            service.Update();
            Assert.Equal(2, service.Sync(42));
        }
        else Assert.Throws<InvalidOperationException>(() => service.Sync(42));
        if (distributed) Assert.Contains(spans, activity => activity.OperationName == "MethodCache.invalidate" && activity.Status != ActivityStatusCode.Error);
        else Assert.Contains(spans, activity => activity.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task GeneratedMethods_CoreTelemetry_PreservesSyncCachingAndMissSpans()
    {
        var spans = new ConcurrentQueue<Activity>();
        var inner = new GeneratedTelemetryService();
        var services = new ServiceCollection();
        services.AddSingleton<IGeneratedTelemetryService>(inner);
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        using var listener = Listen(provider, spans);
        var service = provider.GetRequiredService<IGeneratedTelemetryService>();
        Assert.Equal(1, service.Sync(42));
        Assert.Equal(1, service.Sync(42));
        Assert.Equal(2, await service.Async(42));
        Assert.Equal(2, await service.Async(42));
        service.Update();
        Assert.Equal(3, service.Sync(42));
        Assert.Equal(3, inner.Calls);
        var own = spans.Where(activity => activity.GetTagItem("name") is string method &&
            method.Contains(nameof(IGeneratedTelemetryService), StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, own.Length);
        Assert.All(own, activity => Assert.Equal("MethodCache.load", activity.OperationName));
        Assert.Contains(spans, activity => activity.OperationName == "MethodCache.invalidate");
    }
    private static ActivityListener Listen(ServiceProvider provider, ConcurrentQueue<Activity> spans)
    {
        var owned = provider.GetRequiredService<MethodCache.Core.Telemetry.CoreTelemetry>().Source;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, owned),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

}

public interface IGeneratedTelemetryService
{
    [Cache(Duration = "00:10:00", Tags = ["generated-telemetry"])] int Sync(int id);
    [Cache(Duration = "00:10:00", Tags = ["generated-telemetry"])] Task<int> Async(int id);
    [CacheInvalidate(Tags = ["generated-telemetry"])] void Update();
}

public sealed class GeneratedTelemetryService : IGeneratedTelemetryService
{
    public int Calls { get; private set; }
    public bool Fail { get; init; }
    public int Sync(int id) => Fail ? throw new InvalidOperationException("origin failed") : ++Calls;
    public Task<int> Async(int id) => Task.FromResult(++Calls);
    public void Update() { }
}
