using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Runtime;

/// <summary>
/// Represents the runtime-ready view of a cache policy used when executing cached methods.
/// </summary>
public sealed class CacheRuntimeDescriptor
{
    private CacheRuntimeDescriptor(
        string methodId,
        CachePolicy policy,
        CachePolicyFields fields,
        IReadOnlyDictionary<string, string?> metadata,
        CacheRuntimeOptions runtimeOptions)
    {
        MethodId = methodId;
        Policy = policy;
        Fields = fields;
        Metadata = metadata;
        RuntimeOptions = runtimeOptions;
    }

    public string MethodId { get; }
    public CachePolicy Policy { get; }
    public CachePolicyFields Fields { get; }
    public IReadOnlyDictionary<string, string?> Metadata { get; }
    public CacheRuntimeOptions RuntimeOptions { get; }

    public bool RequireIdempotent => Policy.RequireIdempotent ?? false;
    public TimeSpan? Duration => Policy.Duration;
    public IReadOnlyList<string> Tags => Policy.Tags;
    public Type? KeyGeneratorType => Policy.KeyGeneratorType;
    public int? Version => Policy.Version;

    public static CacheRuntimeDescriptor FromPolicyResult(PolicyResolutionResult result)
    {
        if (result == null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var fields = result.Contributions.Aggregate(
            CachePolicyFields.None,
            static (mask, contribution) => mask | contribution.Fields);

        var metadata = result.Policy.Metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal);

        return new CacheRuntimeDescriptor(result.MethodId, result.Policy, fields, metadata, CacheRuntimeOptions.Empty);
    }

    public static CacheRuntimeDescriptor FromPolicy(string methodId, CachePolicy policy, CachePolicyFields fields, IReadOnlyDictionary<string, string?>? metadata = null, CacheRuntimeOptions? runtimeOptions = null)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method id must be provided.", nameof(methodId));
        }

        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        return new CacheRuntimeDescriptor(methodId, policy, fields, metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal), runtimeOptions ?? CacheRuntimeOptions.Empty);
    }

    public static CacheRuntimeDescriptor FromPolicyDraft(PolicyDraft draft, CacheRuntimeOptions runtimeOptions)
    {
        if (runtimeOptions == null)
        {
            throw new ArgumentNullException(nameof(runtimeOptions));
        }

        var metadata = draft.Metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        return new CacheRuntimeDescriptor(draft.MethodId, draft.Policy, draft.Fields, metadata, runtimeOptions);
    }

}
