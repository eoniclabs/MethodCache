using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Extensions;
using MethodCache.OpenTelemetry.Metrics;
using MethodCache.OpenTelemetry.Propagators;
using MethodCache.OpenTelemetry.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

/// <summary>Telemetry must not leak personal data or create a time series per argument value.</summary>
public class TelemetryPrivacyTests
{
    [Fact]
    public async Task MetricDimensions_LeaveOutCacheTagsAndKeyCounts()
    {
        var methodName = $"Dimensions_{Guid.NewGuid():N}";
        var dimensions = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MetricInstruments.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                dimensions.Add(tag.Key);
            }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();
        var policy = CacheRuntimePolicy.FromPolicy(
            methodName,
            CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(1), Tags = new[] { "user:42" } },
            CachePolicyFields.Duration | CachePolicyFields.Tags);

        await manager.GetOrCreateFastAsync("user:42:profile", methodName, () => Task.FromResult("Ada"), policy);
        await manager.InvalidateByKeysAsync("user:42:profile", "user:43:profile");

        dimensions.Should().Contain("method");
        dimensions.Should().NotContain(new[] { "tags", "keys_count" });
    }

    [Fact]
    public async Task GetOrCreateAsync_GeneratesTheKeyOnce()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();
        var keys = new CountingKeyGenerator();
        var policy = CacheRuntimePolicy.FromPolicy("Once", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(1) }, CachePolicyFields.Duration);

        await manager.GetOrCreateAsync("Once", new object[] { 1 }, () => Task.FromResult("value"), policy, keys);

        keys.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserAndTenantIds_ReachSpans_OnlyWithExportSensitiveData(bool exportSensitiveData)
    {
        var propagator = new BaggagePropagator(Options.Create(new OpenTelemetryOptions { ExportSensitiveData = exportSensitiveData }));

        // Arriving as baggage from an upstream service, and set locally
        var upstream = new Activity("upstream").AddBaggage(TracingConstants.BaggageKeys.CacheUserId, "alice");
        propagator.ExtractBaggage(upstream);
        propagator.SetCacheTenantId("contoso");
        propagator.SetCacheCorrelationId("corr-1");

        var span = new Activity("cache");
        propagator.InjectBaggage(span);

        span.GetBaggageItem(TracingConstants.BaggageKeys.CacheCorrelationId).Should().Be("corr-1");
        span.GetBaggageItem(TracingConstants.BaggageKeys.CacheUserId).Should().Be(exportSensitiveData ? "alice" : null);
        span.GetBaggageItem(TracingConstants.BaggageKeys.CacheTenantId).Should().Be(exportSensitiveData ? "contoso" : null);
    }

    [Fact]
    public async Task ValuesSetInAChildTask_DoNotReachTheParentOrSiblings()
    {
        var propagator = new BaggagePropagator(Options.Create(new OpenTelemetryOptions()));
        propagator.SetCacheTenantId("parent");

        var seen = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            propagator.SetCacheTenantId($"child-{i}");
            await Task.Yield();
            return propagator.GetCacheTenantId() == $"child-{i}";
        })));

        seen.Should().AllBeEquivalentTo(true);
        propagator.GetCacheTenantId().Should().Be("parent");
    }

    private sealed class CountingKeyGenerator : ICacheKeyGenerator
    {
        private int _calls;

        public int Calls => _calls;

        public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy)
        {
            Interlocked.Increment(ref _calls);
            return $"{methodName}:{string.Join(",", args)}";
        }
    }
}
