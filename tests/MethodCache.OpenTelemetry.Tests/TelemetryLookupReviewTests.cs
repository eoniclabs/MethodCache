using OpenTelemetry;
using OpenTelemetry.Trace;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

public class TelemetryLookupReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGet_GeneratesItsKeyOnce(bool instrumented)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddMethodCache();
        using var instrumentation = instrumented ? Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().Build() : null;
        using var provider = services.BuildServiceProvider();
        var generator = new CountingGenerator();
        var manager = provider.GetRequiredService<ICacheManager>();
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("cached"), CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1) });
        Assert.Equal("cached", await manager.TryGetAsync<string>("M", [], CacheRuntimePolicy.Empty("M"), generator));
        Assert.Equal(1, generator.Calls);
    }

    private sealed class CountingGenerator : ICacheKeyGenerator
    {
        public int Calls;
        public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy) { Calls++; return "key"; }
    }
}
