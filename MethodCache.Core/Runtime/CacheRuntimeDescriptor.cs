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
        IReadOnlyDictionary<string, string?> metadata,
        CacheRuntimeOptions runtimeOptions,
        CacheMethodSettings? legacySettings)
    {
        MethodId = methodId;
        Policy = policy;
        Fields = fields;
        Metadata = metadata;
        RuntimeOptions = runtimeOptions;
        _legacySettings = legacySettings;
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

        return new CacheRuntimeDescriptor(result.MethodId, result.Policy, fields, metadata, CacheRuntimeOptions.Empty, null);
    }

    public static CacheRuntimeDescriptor FromPolicy(string methodId, CachePolicy policy, CachePolicyFields fields, IReadOnlyDictionary<string, string?>? metadata = null, CacheRuntimeOptions? runtimeOptions = null, CacheMethodSettings? legacySettings = null)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method id must be provided.", nameof(methodId));
        }

        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        return new CacheRuntimeDescriptor(methodId, policy, fields, metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal), runtimeOptions ?? CacheRuntimeOptions.Empty, legacySettings);
    }

    public static CacheRuntimeDescriptor FromPolicyDraft(PolicyDraft draft, CacheRuntimeOptions runtimeOptions, CacheMethodSettings? legacySettings = null)
    {
        if (runtimeOptions == null)
        {
            throw new ArgumentNullException(nameof(runtimeOptions));
        }

        var metadata = draft.Metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        return new CacheRuntimeDescriptor(draft.MethodId, draft.Policy, draft.Fields, metadata, runtimeOptions, legacySettings);
    }

    /// <summary>
    /// Temporary bridge for legacy runtime paths still expecting <see cref="CacheMethodSettings"/>.
    /// </summary>
    public CacheMethodSettings ToCacheMethodSettings()
    {
        if (_legacySettings != null)
        {
            return _legacySettings.Clone();
        }

        var settings = CachePolicyConversion.ToCacheMethodSettings(Policy);
        settings.SlidingExpiration = RuntimeOptions.SlidingExpiration;
        settings.RefreshAhead = RuntimeOptions.RefreshAhead;
        settings.StampedeProtection = RuntimeOptions.StampedeProtection;
        settings.DistributedLock = RuntimeOptions.DistributedLock;
        settings.Metrics = RuntimeOptions.Metrics;
        return settings;
    }

    private readonly CacheMethodSettings? _legacySettings;
}

/// <summary>
/// Backward-compatibility extensions for ICacheKeyGenerator during migration to descriptors.
/// Will be removed in v4.0.0.
/// </summary>
public static class ICacheKeyGeneratorCompatExtensions
{
    /// <summary>
    /// Legacy overload for generating cache keys from CacheMethodSettings.
    /// Use the CacheRuntimeDescriptor overload instead.
    /// </summary>
    [Obsolete("Use GenerateKey overload with CacheRuntimeDescriptor. Will be removed in v4.0.0")]
    public static string GenerateKey(
        this ICacheKeyGenerator generator,
        string methodName,
        object[] args,
        CacheMethodSettings settings)
    {
        if (generator == null)
        {
            throw new ArgumentNullException(nameof(generator));
        }

        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        // Create a CachePolicy from the settings
        var policy = new CachePolicy
        {
            Duration = settings.Duration,
            Tags = settings.Tags,
            KeyGeneratorType = settings.KeyGeneratorType,
            Version = settings.Version,
            RequireIdempotent = settings.IsIdempotent
        };

        var allFields = CachePolicyFields.Duration | CachePolicyFields.Tags | CachePolicyFields.KeyGenerator |
                        CachePolicyFields.Version | CachePolicyFields.Metadata | CachePolicyFields.RequireIdempotent;

        var descriptor = CacheRuntimeDescriptor.FromPolicy(
            methodName,
            policy,
            allFields,
            null,
            CacheRuntimeOptions.FromLegacySettings(settings),
            settings);

        return generator.GenerateKey(methodName, args, descriptor);
    }
}
