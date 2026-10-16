using System.Collections.Concurrent;
using System.Diagnostics;
using MethodCache.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class GeneratedTelemetryContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedSync_TelemetryWrapper_RecordsOriginAndInvalidationFailures(bool distributed)
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TracingConstants.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var inner = new GeneratedTelemetryService { Fail = !distributed };
        var services = new ServiceCollection();
        services.AddSingleton<IGeneratedTelemetryService>(inner);
        services.AddMethodCache();
        if (distributed) services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        services.AddMethodCacheOpenTelemetry();
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IGeneratedTelemetryService>();
        if (distributed)
        {
            Assert.Equal(1, service.Sync(42));
            Assert.Throws<NotSupportedException>(() => service.Update());
            Assert.Equal(2, service.Sync(42));
        }
        else Assert.Throws<InvalidOperationException>(() => service.Sync(42));
        Assert.Contains(spans, activity => activity.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task GeneratedMethods_TelemetryWrapper_PreservesSyncCachingAndMissSpans()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TracingConstants.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var inner = new GeneratedTelemetryService();
        var services = new ServiceCollection();
        services.AddSingleton<IGeneratedTelemetryService>(inner);
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IGeneratedTelemetryService>();
        Assert.Equal(1, service.Sync(42));
        Assert.Equal(1, service.Sync(42));
        Assert.Equal(2, await service.Async(42));
        Assert.Equal(2, await service.Async(42));
        service.Update();
        Assert.Equal(3, service.Sync(42));
        Assert.Equal(3, inner.Calls);
        var own = spans.Where(activity => activity.GetTagItem(TracingConstants.AttributeNames.CacheMethod) is string method &&
            method.Contains(nameof(IGeneratedTelemetryService), StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, own.Length);
        Assert.All(own, activity => Assert.Equal(false, activity.GetTagItem(TracingConstants.AttributeNames.CacheHit)));
        Assert.Contains(spans, activity => Equals(activity.GetTagItem("cache.operation"), TracingConstants.Operations.Delete));
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
