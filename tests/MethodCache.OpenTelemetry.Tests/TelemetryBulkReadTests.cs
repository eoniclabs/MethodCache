using OpenTelemetry;
using OpenTelemetry.Trace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MethodCache.TestSupport;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

/// <summary>
/// A bulk read through the instrumented cache manager reads the distributed cache for its memory misses before it
/// runs the bulk factory, as it does without instrumentation.
/// </summary>
public sealed class TelemetryBulkReadTests : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);

    private readonly IDistributedCache _shared = new AtomicTestCache();
    private readonly List<ServiceProvider> _providers = new();
    private readonly TracerProvider _instrumentation = Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().Build();

    public void Dispose()
    {
        _instrumentation.Dispose();
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private ICacheManager CreateNode()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => _shared, options => options.ClockSkewTolerance = TimeSpan.Zero);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var manager = provider.GetRequiredService<ICacheManager>();
        manager.Should().BeOfType<MethodCache.Extensibility.InMemoryCacheManager>();
        return manager;
    }

    [Fact]
    public async Task GetOrCreateManyAsync_KeyOnlyInTheDistributedCache_IsNotPassedToTheFactory()
    {
        var a = CreateNode();
        var b = CreateNode();
        (await a.GetOrCreateAsync("stored", (_, _) => ValueTask.FromResult("A"), options => options.WithDuration(Duration)))
            .Should().Be("A");

        var requested = new List<string>();
        var result = await b.GetOrCreateManyAsync<string>(
            new[] { "stored", "missing" },
            (keys, _, _) =>
            {
                requested.AddRange(keys);
                return ValueTask.FromResult<IDictionary<string, string>>(keys.ToDictionary(key => key, _ => "from factory"));
            },
            options => options.WithDuration(Duration));

        requested.Should().Equal("missing");
        result.Should().Equal(new Dictionary<string, string> { ["stored"] = "A", ["missing"] = "from factory" });
        b.TryGetFast<string>("stored", out var inMemory).Should().BeTrue("the distributed hit is now in B's memory");
        inMemory.Should().Be("A");
    }
}
