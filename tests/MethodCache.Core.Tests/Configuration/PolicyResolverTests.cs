using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MethodCache.Core.Configuration.Surfaces.ConfigFile;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        // Tags combine: the override adds its tags to the attribute's, so [CacheInvalidate] on "attr" still applies
        Assert.Equal(new[] { "attr", "override" }, result.Policy.Tags);

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

    [Fact]
    public async Task FluentDefaults_ReachAttributeOnlyMethods_AndAttributeValuesWin()
    {
        var fluent = new FluentPolicySource(config =>
        {
            config.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(1)).WithSlidingExpiration(TimeSpan.FromSeconds(30)));
            config.ForGroup("orders").Configure(o => o.RefreshAhead(TimeSpan.FromSeconds(10)).WithSlidingExpiration(TimeSpan.FromSeconds(20)));
            config.ForGroup("users").Configure(o => o.WithVersion(3));
        });

        var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(CreateAttributeSource(TimeSpan.FromMinutes(5), group: "orders"), PolicySourcePriority.Attributes),
            new PolicySourceRegistration(fluent, PolicySourcePriority.StartupFluent)
        });

        var policy = (await resolver.ResolveAsync(MethodId)).Policy;

        Assert.Equal(TimeSpan.FromMinutes(5), policy.Duration);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.SlidingExpiration);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.RefreshAhead);
        Assert.Null(policy.Version);
        Assert.DoesNotContain(await resolver.GetAllAsync(), r => MethodIds.IsDefaults(r.MethodId));

        await resolver.DisposeAsync();
    }

    [Fact]
    public async Task ConfigurationDefaults_ReachAttributeOnlyMethods_AndFollowReloads()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MethodCache:Defaults:SlidingExpiration"] = "00:00:30" })
            .Build();

        var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(CreateAttributeSource(TimeSpan.FromMinutes(5)), PolicySourcePriority.Attributes),
            new PolicySourceRegistration(ConfigFilePolicySourceBuilder.FromConfiguration(configuration), PolicySourcePriority.ConfigurationFiles)
        });

        var initial = (await resolver.ResolveAsync(MethodId)).Policy;
        Assert.Equal(TimeSpan.FromMinutes(5), initial.Duration);
        Assert.Equal(TimeSpan.FromSeconds(30), initial.SlidingExpiration);

        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var watcher = resolver.WatchAsync(MethodId, watchCts.Token).GetAsyncEnumerator(watchCts.Token);
        var nextChange = watcher.MoveNextAsync();

        configuration["MethodCache:Defaults:SlidingExpiration"] = "00:01:00";
        configuration.Reload();

        Assert.True(await nextChange);
        Assert.Equal(TimeSpan.FromMinutes(1), watcher.Current.Policy.SlidingExpiration);
        Assert.Equal(TimeSpan.FromMinutes(5), watcher.Current.Policy.Duration);

        await watcher.DisposeAsync();
        await resolver.DisposeAsync();
    }

    [Fact]
    public async Task PolicyForAMethodWithoutCache_IsReportedOnce()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MethodCache:Defaults:Duration"] = "00:01:00",
                [$"MethodCache:Services:{MethodId}:Duration"] = "00:02:00",
                ["MethodCache:Services:My.IOrderService.GetOrdr:Duration"] = "00:03:00"
            })
            .Build();
        var logger = new ListLogger();

        var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(CreateAttributeSource(TimeSpan.FromMinutes(5)), PolicySourcePriority.Attributes),
            new PolicySourceRegistration(ConfigFilePolicySourceBuilder.FromConfiguration(configuration), PolicySourcePriority.ConfigurationFiles)
        }, logger);

        await resolver.ResolveAsync(MethodId);
        await resolver.ResolveAsync("My.IOrderService.GetOrdr");

        var warning = Assert.Single(logger.Messages);
        Assert.Contains("My.IOrderService.GetOrdr", warning);

        await resolver.DisposeAsync();
    }

    [Fact]
    public async Task WithoutAttributeSources_NoMethodIsReportedAsUnknown()
    {
        var logger = new ListLogger();
        var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(CreateSource("runtime", TimeSpan.FromMinutes(1)), PolicySourcePriority.RuntimeOverrides)
        }, logger);

        await resolver.ResolveAsync(MethodId);

        Assert.Empty(logger.Messages);
        await resolver.DisposeAsync();
    }

    private sealed class ListLogger : ILogger<PolicyResolver>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    private static TestPolicySource CreateAttributeSource(TimeSpan duration, string? group = null)
    {
        var metadata = group == null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["group"] = group };
        var policy = CachePolicy.Empty with { Duration = duration, Metadata = metadata };
        var fields = CachePolicyFields.Duration | (group == null ? CachePolicyFields.None : CachePolicyFields.Metadata);
        var snapshot = PolicySnapshotBuilder.FromPolicy(PolicySourceIds.Attributes, MethodId, policy, fields, DateTimeOffset.UtcNow, metadata);
        return new TestPolicySource(PolicySourceIds.Attributes, new[] { snapshot });
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
