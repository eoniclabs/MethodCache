using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Diagnostics;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Registry;
using MethodCache.Core.Configuration.Resolver;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class PolicyDiagnosticsServiceTests
{
    private const string MethodId = "MethodCache.Core.Tests.Configuration.PolicyDiagnosticsServiceTests+ITestService.Get";

    [Fact]
    public async Task GetPolicy_ReturnsEffectivePolicyAndContributionsAsync()
    {
        var attributeSettings = new CacheMethodSettings
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "attr" },
            IsIdempotent = true
        };

        var runtimeSettings = new CacheMethodSettings
        {
            Duration = TimeSpan.FromMinutes(1),
            Tags = new List<string> { "runtime" }
        };

        var attributeSource = new TestPolicySource(PolicySourceIds.Attributes, new[] { CreateSnapshot(PolicySourceIds.Attributes, attributeSettings) });
        var runtimeSource = new TestPolicySource(PolicySourceIds.RuntimeOverrides, Array.Empty<PolicySnapshot>());

        var registrations = new[]
        {
            new PolicySourceRegistration(attributeSource, 10),
            new PolicySourceRegistration(runtimeSource, 100)
        };

        var resolver = new PolicyResolver(registrations);
        await resolver.ResolveAsync(MethodId);

        var registry = new PolicyRegistry(resolver, registrations);
        var diagnostics = new PolicyDiagnosticsService(registry);

        // Baseline from attributes
        var baseline = diagnostics.GetPolicy(MethodId);
        Assert.Equal(TimeSpan.FromMinutes(5), baseline.Policy.Duration);
        Assert.Contains("attr", baseline.Policy.Tags);
        Assert.Contains(baseline.ContributionsBySource.Keys, key => key == PolicySourceIds.Attributes);

        // Runtime override should take precedence and appear in contributions
        runtimeSource.EmitChange(CreateChange(runtimeSettings, PolicySourceIds.RuntimeOverrides, PolicyChangeReason.Updated));
        var overridden = await WaitForDurationAsync(diagnostics, TimeSpan.FromMinutes(1));
        Assert.Contains("runtime", overridden.Policy.Tags);
        Assert.Contains(overridden.ContributionsBySource.Keys, key => key == PolicySourceIds.RuntimeOverrides);

        // Removal should fall back to attribute configuration
        runtimeSource.EmitChange(CreateRemoval(PolicySourceIds.RuntimeOverrides));
        var reverted = await WaitForDurationAsync(diagnostics, TimeSpan.FromMinutes(5));
        Assert.Contains("attr", reverted.Policy.Tags);
        Assert.DoesNotContain(reverted.ContributionsBySource.Keys, key => key == PolicySourceIds.RuntimeOverrides);
    }

    [Fact]
    public async Task GetContributionsAndFindBySource_WorkAcrossReportsAsync()
    {
        var attributeSettings = new CacheMethodSettings
        {
            Duration = TimeSpan.FromMinutes(2),
            Tags = new List<string> { "attr" }
        };

        var attributeSource = new TestPolicySource(PolicySourceIds.Attributes, new[] { CreateSnapshot(PolicySourceIds.Attributes, attributeSettings) });
        var registrations = new[] { new PolicySourceRegistration(attributeSource, 10) };

        var resolver = new PolicyResolver(registrations);
        await resolver.ResolveAsync(MethodId);

        var registry = new PolicyRegistry(resolver, registrations);
        var diagnostics = new PolicyDiagnosticsService(registry);

        var bySource = diagnostics.FindBySource(PolicySourceIds.Attributes);
        Assert.Single(bySource);
        Assert.Equal(MethodId, bySource[0].MethodId);

        var contributions = diagnostics.GetContributions(MethodId, PolicySourceIds.Attributes);
        Assert.NotEmpty(contributions);
        Assert.All(contributions, c => Assert.Equal(PolicySourceIds.Attributes, c.SourceId));
    }

    private static PolicySnapshot CreateSnapshot(string sourceId, CacheMethodSettings settings)
    {
        var (policy, fields) = CachePolicyMapper.FromSettings(settings);
        var enriched = CachePolicyMapper.AttachContribution(policy, sourceId, fields, DateTimeOffset.UtcNow);
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source"] = sourceId
        };

        return new PolicySnapshot(sourceId, MethodId, enriched, DateTimeOffset.UtcNow, metadata);
    }

    private static PolicyChange CreateChange(CacheMethodSettings settings, string sourceId, PolicyChangeReason reason)
    {
        var (policy, fields) = CachePolicyMapper.FromSettings(settings);
        if (fields == CachePolicyFields.None)
        {
            fields = CachePolicyMapper.DetectFields(policy);
        }

        var enriched = CachePolicyMapper.AttachContribution(policy, sourceId, fields, DateTimeOffset.UtcNow);
        var delta = new CachePolicyDelta(fields, CachePolicyFields.None, enriched);
        return new PolicyChange(sourceId, MethodId, delta, reason, DateTimeOffset.UtcNow);
    }

    private static PolicyChange CreateRemoval(string sourceId)
    {
        var fieldsToClear = CachePolicyFields.Duration |
                            CachePolicyFields.Tags |
                            CachePolicyFields.KeyGenerator |
                            CachePolicyFields.Version |
                            CachePolicyFields.Metadata |
                            CachePolicyFields.RequireIdempotent;

        var delta = new CachePolicyDelta(CachePolicyFields.None, fieldsToClear, CachePolicy.Empty);
        return new PolicyChange(sourceId, MethodId, delta, PolicyChangeReason.Removed, DateTimeOffset.UtcNow);
    }

    private interface ITestService
    {
        Task<string> Get();
    }

    private sealed class TestPolicySource : IPolicySource
    {
        private readonly Channel<PolicyChange> _channel = Channel.CreateUnbounded<PolicyChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        private readonly IReadOnlyCollection<PolicySnapshot> _snapshots;

        public TestPolicySource(string sourceId, IReadOnlyCollection<PolicySnapshot> snapshots)
        {
            SourceId = sourceId;
            _snapshots = snapshots;
        }

        public string SourceId { get; }

        public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_snapshots);
        }

        public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }

        public void EmitChange(PolicyChange change)
        {
            _channel.Writer.TryWrite(change);
        }
    }
    private static async Task<PolicyDiagnosticsReport> WaitForDurationAsync(PolicyDiagnosticsService diagnostics, TimeSpan expected)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow <= timeout)
        {
            var report = diagnostics.GetPolicy(MethodId);
            if (report.Policy.Duration == expected)
            {
                return report;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for policy duration {expected}.");
    }
}
