using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.Runtime;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class PolicyResolverTests
{
    private const string MethodId = "MethodCache.Core.Tests.Configuration.PolicyResolverTests+ITestService.Get";

    [Fact]
    public async Task ResolveAsync_RespectsPrecedence()
    {
        var attributeSource = CreateSource(PolicySourceIds.Attributes, TimeSpan.FromMinutes(5), new[] { "attr" });
        var runtimeSource = CreateSource(PolicySourceIds.RuntimeOverrides, TimeSpan.FromMinutes(1), new[] { "override" });

        var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(attributeSource, Priority: 10),
            new PolicySourceRegistration(runtimeSource, Priority: 100)
        });

        var result = await resolver.ResolveAsync(MethodId);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Policy.Duration);
        Assert.Contains("override", result.Policy.Tags);
        Assert.DoesNotContain("attr", result.Policy.Tags);

        await resolver.DisposeAsync();
    }

    [Fact]
    public async Task WatchAsync_EmitsUpdatesAndRemovals()
    {
        var attributeSource = CreateSource(PolicySourceIds.Attributes, TimeSpan.FromMinutes(5), new[] { "attr" });
        var runtimeSource = new TestPolicySource(PolicySourceIds.RuntimeOverrides, Array.Empty<PolicySnapshot>());

        var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(attributeSource, Priority: 10),
            new PolicySourceRegistration(runtimeSource, Priority: 100)
        });

        _ = await resolver.ResolveAsync(MethodId);

        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var watcher = resolver.WatchAsync(MethodId, watchCts.Token).GetAsyncEnumerator(watchCts.Token);

        // The first MoveNextAsync subscribes to changes before it waits, so nothing emitted below can be missed
        var nextChange = watcher.MoveNextAsync();

        runtimeSource.EmitChange(CreateChange(PolicySourceIds.RuntimeOverrides, TimeSpan.FromMinutes(1), new[] { "override" }, PolicyChangeReason.Updated));

        Assert.True(await nextChange);
        var update = watcher.Current;
        Assert.Equal(TimeSpan.FromMinutes(1), update.Policy.Duration);
        Assert.Contains("override", update.Policy.Tags);

        var interimResult = await resolver.ResolveAsync(MethodId);
        Assert.Equal(MethodId, interimResult.MethodId);
        Assert.Equal(TimeSpan.FromMinutes(1), interimResult.Policy.Duration);
        Assert.Contains("override", interimResult.Policy.Tags);

        nextChange = watcher.MoveNextAsync();
        runtimeSource.EmitChange(CreateRemovalChange(PolicySourceIds.RuntimeOverrides));

        Assert.True(await nextChange);
        var removal = watcher.Current;
        Assert.Equal(MethodId, removal.MethodId);
        Assert.Equal(TimeSpan.FromMinutes(5), removal.Policy.Duration);
        Assert.Contains("attr", removal.Policy.Tags);

        await watcher.DisposeAsync();
        await resolver.DisposeAsync();
    }

    private static TestPolicySource CreateSource(string sourceId, TimeSpan duration, IEnumerable<string>? tags = null)
    {
        var policy = CachePolicy.Empty with
        {
            Duration = duration,
            Tags = tags?.ToArray() ?? Array.Empty<string>()
        };
        var fields = CachePolicyFields.Duration;
        if (tags?.Any() == true)
        {
            fields |= CachePolicyFields.Tags;
        }

        var snapshot = PolicySnapshotBuilder.FromPolicy(
            sourceId,
            MethodId,
            policy,
            fields,
            DateTimeOffset.UtcNow,
            policy.Metadata);
        return new TestPolicySource(sourceId, new[] { snapshot });
    }

    private static PolicyChange CreateChange(string sourceId, TimeSpan duration, IEnumerable<string>? tags, PolicyChangeReason reason)
    {
        var policy = CachePolicy.Empty with
        {
            Duration = duration,
            Tags = tags?.ToArray() ?? Array.Empty<string>()
        };
        var fields = CachePolicyFields.Duration;
        if (tags?.Any() == true)
        {
            fields |= CachePolicyFields.Tags;
        }

        var enriched = CachePolicyMapper.AttachContribution(policy, sourceId, fields, DateTimeOffset.UtcNow);
        var delta = new CachePolicyDelta(fields == CachePolicyFields.None ? CachePolicyMapper.DetectFields(enriched) : fields, CachePolicyFields.None, enriched);
        return new PolicyChange(sourceId, MethodId, delta, reason, DateTimeOffset.UtcNow);
    }

    private static PolicyChange CreateRemovalChange(string sourceId)
    {
        var delta = new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.Duration | CachePolicyFields.Tags | CachePolicyFields.SlidingExpiration | CachePolicyFields.Version | CachePolicyFields.Metadata | CachePolicyFields.RefreshAhead, CachePolicy.Empty);
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
}
