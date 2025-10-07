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
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Resolver;
using MethodCache.Core.Configuration.Sources;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class PolicyResolverTests
{
    private const string MethodId = "MethodCache.Core.Tests.Configuration.PolicyResolverTests+ITestService.Get";

    [Fact]
    public async Task ResolveAsync_RespectsPrecedence()
    {
        var attributeSource = CreateSource(PolicySourceIds.Attributes, CreateSettings(TimeSpan.FromMinutes(5), tags: new[] { "attr" }));
        var runtimeSource = CreateSource(PolicySourceIds.RuntimeOverrides, CreateSettings(TimeSpan.FromMinutes(1), tags: new[] { "override" }));

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
        var attributeSource = CreateSource(PolicySourceIds.Attributes, CreateSettings(TimeSpan.FromMinutes(5), tags: new[] { "attr" }));
        var runtimeSource = new TestPolicySource(PolicySourceIds.RuntimeOverrides, Array.Empty<PolicySnapshot>());

        var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(attributeSource, Priority: 10),
            new PolicySourceRegistration(runtimeSource, Priority: 100)
        });

        _ = await resolver.ResolveAsync(MethodId);

        var changes = new List<PolicyResolutionResult>();
        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var watchTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in resolver.WatchAsync(MethodId, watchCts.Token))
                {
                    lock (changes)
                    {
                        changes.Add(change);
                        if (changes.Count >= 2)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling the watch
            }
        });

        await Task.Delay(50);

        runtimeSource.EmitChange(CreateChange(PolicySourceIds.RuntimeOverrides, CreateSettings(TimeSpan.FromMinutes(1), tags: new[] { "override" }), PolicyChangeReason.Updated));
        await Task.Delay(100);
        var interimResult = await resolver.ResolveAsync(MethodId);
        Assert.Equal(MethodId, interimResult.MethodId);
        Assert.Equal(TimeSpan.FromMinutes(1), interimResult.Policy.Duration);
        Assert.Contains("override", interimResult.Policy.Tags);

        await WaitForCountAsync(changes, 1);

        var update = changes[0];
        Assert.Equal(TimeSpan.FromMinutes(1), update.Policy.Duration);
        Assert.Contains("override", update.Policy.Tags);

        runtimeSource.EmitChange(CreateRemovalChange(PolicySourceIds.RuntimeOverrides));
        await WaitForCountAsync(changes, 2);

        var removal = changes[1];
        Assert.Equal(MethodId, removal.MethodId);
        Assert.Equal(TimeSpan.FromMinutes(5), removal.Policy.Duration);
        Assert.Contains("attr", removal.Policy.Tags);

        watchCts.Cancel();
        await watchTask;
        await resolver.DisposeAsync();
    }

    private static TestPolicySource CreateSource(string sourceId, CacheMethodSettings settings)
    {
        var entry = new MethodCacheConfigEntry
        {
            ServiceType = "MethodCache.Core.Tests.Configuration.PolicyResolverTests+ITestService",
            MethodName = "Get",
            Settings = settings,
            Priority = 0
        };

        var snapshot = PolicySnapshotBuilder.FromConfigEntry(sourceId, entry, DateTimeOffset.UtcNow);
        return new TestPolicySource(sourceId, new[] { snapshot });
    }

    private static CacheMethodSettings CreateSettings(TimeSpan duration, IEnumerable<string>? tags = null)
    {
        return new CacheMethodSettings
        {
            Duration = duration,
            Tags = tags?.ToList() ?? new List<string>()
        };
    }

    private static PolicyChange CreateChange(string sourceId, CacheMethodSettings settings, PolicyChangeReason reason)
    {
        var (policy, fields) = CachePolicyMapper.FromSettings(settings);
        var enriched = CachePolicyMapper.AttachContribution(policy, sourceId, fields, DateTimeOffset.UtcNow);
        var delta = new CachePolicyDelta(fields == CachePolicyFields.None ? CachePolicyMapper.DetectFields(enriched) : fields, CachePolicyFields.None, enriched);
        return new PolicyChange(sourceId, MethodId, delta, reason, DateTimeOffset.UtcNow);
    }

    private static PolicyChange CreateRemovalChange(string sourceId)
    {
        var delta = new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.Duration | CachePolicyFields.Tags | CachePolicyFields.KeyGenerator | CachePolicyFields.Version | CachePolicyFields.Metadata | CachePolicyFields.RequireIdempotent, CachePolicy.Empty);
        return new PolicyChange(sourceId, MethodId, delta, PolicyChangeReason.Removed, DateTimeOffset.UtcNow);
    }

    private static async Task WaitForCountAsync(List<PolicyResolutionResult> list, int expected)
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            lock (list)
            {
                if (list.Count >= expected)
                {
                    return;
                }
            }

            if ((DateTime.UtcNow - start) > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException($"Expected {expected} policy changes but observed fewer within the timeout.");
            }

            await Task.Delay(50);
        }
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
