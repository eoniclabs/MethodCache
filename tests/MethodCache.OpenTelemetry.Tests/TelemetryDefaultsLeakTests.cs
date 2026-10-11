using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

/// <summary>
/// Pins the OpenTelemetry threat in docs/security.md: with the default options, no cache key, argument value or
/// tag value reaches a span (attributes, events, status, baggage) or a metric. Every operation below puts the same
/// personal value into its key, arguments, tags and pattern, then everything MethodCache exported is searched for it.
/// </summary>
public class TelemetryDefaultsLeakTests
{
    private const string Secret = "alice@example.com";

    [Fact]
    public async Task DefaultOptions_ExportNoKeysArgumentsOrTags()
    {
        var method = $"Leak_{Guid.NewGuid():N}";
        var (spans, metricTags) = await RunEveryOperationAsync(method, configure: null);

        spans.Should().NotBeEmpty();
        spans.Select(s => s.GetTagItem(TracingConstants.AttributeNames.CacheMethod)).Should().Contain(method);
        foreach (var span in spans)
        {
            Exported(span).Should().NotContain(text => text.Contains(Secret, StringComparison.OrdinalIgnoreCase), $"span {span.DisplayName} must not carry the argument");
        }

        metricTags.Should().NotBeEmpty();
        metricTags.Should().NotContain(text => text.Contains(Secret, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecordCacheKeys_RecordsAHash_NotTheKey()
    {
        var method = $"Hash_{Guid.NewGuid():N}";
        var (spans, _) = await RunEveryOperationAsync(method, o => o.RecordCacheKeys = true);

        spans.Should().Contain(s => s.GetTagItem(TracingConstants.AttributeNames.CacheKeyHash) != null);
        spans.SelectMany(Exported).Should().NotContain(text => text.Contains(Secret, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OptingIn_RecordsKeysTagsAndPatterns()
    {
        // The options that do export these values, so the test above can't pass by exporting nothing at all
        var method = $"OptIn_{Guid.NewGuid():N}";
        var (spans, metricTags) = await RunEveryOperationAsync(method, o =>
        {
            o.RecordCacheKeys = true;
            o.HashCacheKeys = false;
            o.RecordCacheTags = true;
        });

        // Metric dimensions stay low-cardinality whatever the span options are
        metricTags.Should().NotContain(text => text.Contains(Secret, StringComparison.OrdinalIgnoreCase));

        spans.Should().Contain(s => (s.GetTagItem(TracingConstants.AttributeNames.CacheKey) as string) == $"user:{Secret}:profile");
        spans.Should().Contain(s => (s.GetTagItem(TracingConstants.AttributeNames.CacheTags) as string) == $"user:{Secret}");
        spans.Should().Contain(s => (s.GetTagItem("cache.pattern") as string) == $"user:{Secret}*");
    }

    private static IEnumerable<string> Exported(Activity span)
    {
        yield return span.DisplayName;
        yield return span.OperationName;
        yield return span.StatusDescription ?? "";
        foreach (var tag in span.TagObjects)
        {
            yield return tag.Key + "=" + tag.Value;
        }

        foreach (var item in span.Baggage)
        {
            yield return item.Key + "=" + item.Value;
        }

        foreach (var e in span.Events)
        {
            yield return e.Name;
            foreach (var tag in e.Tags)
            {
                yield return tag.Key + "=" + tag.Value;
            }
        }
    }

    private static async Task<(List<Activity> Spans, List<string> MetricTags)> RunEveryOperationAsync(string method, Action<OpenTelemetryOptions>? configure)
    {
        var exported = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(TracingConstants.ActivitySourceName)
            .AddInMemoryExporter(exported)
            .Build()!;

        var metricTags = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MetricInstruments.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        void Record(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var isOurs = false;
            var texts = new List<string>();
            foreach (var tag in tags)
            {
                isOurs |= tag.Key == "method" && tag.Value as string is { } m && (m == method || m.StartsWith("Invalidate", StringComparison.Ordinal));
                texts.Add(tag.Key + "=" + tag.Value);
            }

            if (isOurs)
            {
                foreach (var text in texts)
                {
                    metricTags.Add(text);
                }
            }
        }
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Record(tags));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Record(tags));
        listener.Start();

        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry(configure);
        await using (var provider = services.BuildServiceProvider())
        {
            var manager = provider.GetRequiredService<ICacheManager>();
            var baggage = provider.GetRequiredService<IBaggagePropagator>();
            baggage.SetCacheUserId(Secret);
            baggage.SetCacheTenantId(Secret);
            baggage.SetCacheCorrelationId(method);

            var policy = CacheRuntimePolicy.FromPolicy(
                method,
                CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(1), Tags = new[] { $"user:{Secret}" } },
                CachePolicyFields.Duration | CachePolicyFields.Tags);
            var key = $"user:{Secret}:profile";

            await manager.GetOrCreateFastAsync(key, method, () => Task.FromResult("profile"), policy); // miss
            await manager.GetOrCreateFastAsync(key, method, () => Task.FromResult("profile"), policy); // hit
            await manager.GetOrCreateAsync(method, new object[] { Secret }, () => Task.FromResult("by args"), policy, new FastHashKeyGenerator());
            await manager.TryGetAsync<string>(method, new object[] { Secret }, policy, new FastHashKeyGenerator());
            var failing = () => manager.GetOrCreateFastAsync<string>($"fail:{Secret}", method, () => throw new InvalidOperationException("load failed"), policy);
            await failing.Should().ThrowAsync<InvalidOperationException>();
            await manager.InvalidateByTagsAsync($"user:{Secret}");
            await manager.InvalidateByKeysAsync(key);
            await manager.InvalidateByTagPatternAsync($"user:{Secret}*");
        }

        tracer.ForceFlush();
        var ours = exported.Where(s => s.GetTagItem(TracingConstants.AttributeNames.CacheMethod) is string m &&
                                       (m == method || m.StartsWith("Invalidate", StringComparison.Ordinal)) &&
                                       s.Baggage.Any(b => b.Value == method))
            .ToList();
        return (ours, metricTags.ToList());
    }
}
