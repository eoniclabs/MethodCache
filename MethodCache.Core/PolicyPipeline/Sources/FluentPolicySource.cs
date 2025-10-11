using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Sources;

internal sealed class FluentPolicySource : IPolicySource
{
    private readonly IReadOnlyList<CompiledPolicy> _compiledPolicies;
    private readonly string _sourceId;

    public FluentPolicySource(Action<IFluentMethodCacheConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _sourceId = PolicySourceIds.StartupFluent;
        _compiledPolicies = CompilePolicies(configure);
    }

    public string SourceId => _sourceId;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>(_compiledPolicies.Count);

        foreach (var policy in _compiledPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(PolicySnapshotBuilder.FromPolicy(_sourceId, policy.MethodId, policy.Policy, policy.Fields, timestamp, policy.Metadata));
        }

        return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots);
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);

    private static IReadOnlyList<CompiledPolicy> CompilePolicies(Action<IFluentMethodCacheConfiguration> configure)
    {
        var fluent = new FluentMethodCacheConfiguration();
        configure(fluent);

        var drafts = fluent.BuildMethodPolicies();
        var compiled = new List<CompiledPolicy>(drafts.Count);

        foreach (var draft in drafts)
        {
            var (policy, fields) = CachePolicyMapper.FromSettings(draft.Settings);
            IReadOnlyDictionary<string, string?>? metadata = null;
            if (!string.IsNullOrWhiteSpace(draft.GroupName))
            {
                metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["group"] = draft.GroupName
                };
            }

            compiled.Add(new CompiledPolicy(draft.MethodKey, policy, fields, metadata));
        }

        return compiled;
    }

    private readonly struct CompiledPolicy
    {
        public CompiledPolicy(string methodId, CachePolicy policy, CachePolicyFields fields, IReadOnlyDictionary<string, string?>? metadata)
        {
            MethodId = methodId;
            Policy = policy;
            Fields = fields;
            Metadata = metadata;
        }

        public string MethodId { get; }
        public CachePolicy Policy { get; }
        public CachePolicyFields Fields { get; }
        public IReadOnlyDictionary<string, string?>? Metadata { get; }
    }
}
