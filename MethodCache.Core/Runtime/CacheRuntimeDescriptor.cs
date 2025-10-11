using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Runtime;

/// <summary>
/// Represents the runtime-ready view of a cache policy used when executing cached methods.
/// Provides helpers for legacy settings conversion while the runtime migrates away from <see cref="CacheMethodSettings"/>.
/// </summary>
public sealed class CacheRuntimeDescriptor
{
    private CacheRuntimeDescriptor(
        string methodId,
        CachePolicy policy,
        CachePolicyFields fields,
        IReadOnlyDictionary<string, string?> metadata)
    {
        MethodId = methodId;
        Policy = policy;
        Fields = fields;
        Metadata = metadata;
    }

    public string MethodId { get; }
    public CachePolicy Policy { get; }
    public CachePolicyFields Fields { get; }
    public IReadOnlyDictionary<string, string?> Metadata { get; }

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

        return new CacheRuntimeDescriptor(result.MethodId, result.Policy, fields, metadata);
    }

    /// <summary>
    /// Temporary bridge for legacy runtime paths still expecting <see cref="CacheMethodSettings"/>.
    /// </summary>
    public CacheMethodSettings ToCacheMethodSettings()
        => CachePolicyConversion.ToCacheMethodSettings(Policy);
}
