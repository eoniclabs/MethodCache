using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime.Options;

namespace MethodCache.Core.Runtime.Core
{
    /// <summary>
    /// The definitive runtime representation of a cache policy, containing all
    /// information needed for cache operations at runtime.
    /// </summary>
    /// <remarks>
    /// This is the authoritative runtime policy object that flows from the resolver
    /// to cache managers and key generators. It packages together the resolved policy,
    /// runtime options, metadata, and precomputed indexes in an immutable record.
    /// </remarks>
    public sealed record CacheRuntimePolicy
    {
        /// <summary>
        /// The unique identifier of the method this policy applies to.
        /// </summary>
        public required string MethodId { get; init; }

        // ============= Core Policy Data (from CachePolicy) =============

        /// <summary>
        /// The duration for which cached values should be retained.
        /// </summary>
        public TimeSpan? Duration { get; init; }

        /// <summary>
        /// Tags associated with cache entries for bulk invalidation.
        /// </summary>
        public IReadOnlyCollection<string> Tags { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Version number for cache key generation to support cache busting.
        /// </summary>
        public int? Version { get; init; }

        /// <summary>
        /// Whether the method must be idempotent for caching.
        /// </summary>
        public bool? RequireIdempotent { get; init; }

        /// <summary>
        /// The type of key generator to use for this method.
        /// </summary>
        public Type? KeyGeneratorType { get; init; }

        // ============= Runtime Options (from CacheRuntimeOptions) =============

        /// <summary>
        /// Sliding expiration window for cache entries.
        /// </summary>
        public TimeSpan? SlidingExpiration { get; init; }

        /// <summary>
        /// Time window before expiration to refresh cache entries.
        /// </summary>
        public TimeSpan? RefreshAhead { get; init; }

        /// <summary>
        /// Configuration for stampede protection.
        /// </summary>
        public StampedeProtectionOptions? StampedeProtection { get; init; }

        /// <summary>
        /// Configuration for distributed locking.
        /// </summary>
        public DistributedLockOptions? DistributedLock { get; init; }

        // ============= Metadata & Indexing =============

        /// <summary>
        /// Additional metadata associated with the policy.
        /// </summary>
        public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
            new Dictionary<string, string?>(StringComparer.Ordinal);

        /// <summary>
        /// Flags indicating which fields have been explicitly set.
        /// </summary>
        public CachePolicyFields Fields { get; init; } = CachePolicyFields.None;

        // ============= Computed Properties =============

        /// <summary>
        /// Indicates whether a valid duration is configured.
        /// </summary>
        public bool HasDuration => Duration.HasValue && Duration.Value > TimeSpan.Zero;

        /// <summary>
        /// Indicates whether tags are configured.
        /// </summary>
        public bool HasTags => Tags.Count > 0;

        /// <summary>
        /// Indicates whether a version is configured.
        /// </summary>
        public bool HasVersion => Version.HasValue;

        /// <summary>
        /// Indicates whether any runtime options are configured.
        /// </summary>
        public bool HasRuntimeOptions =>
            SlidingExpiration.HasValue ||
            RefreshAhead.HasValue ||
            StampedeProtection != null ||
            DistributedLock != null;

        // ============= Factory Methods =============

        /// <summary>
        /// Creates a runtime policy from a policy resolution result.
        /// </summary>
        public static CacheRuntimePolicy FromResolverResult(PolicyResolutionResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);

            // Preserve metadata from the resolved policy
            if (result.Policy.Metadata != null)
            {
                foreach (var kvp in result.Policy.Metadata)
                {
                    metadata[kvp.Key] = kvp.Value;
                }
            }

            // Extract runtime options from metadata if present
            var runtimeOptions = ExtractRuntimeOptions(metadata);

            // Compute fields from contributions
            var fields = result.Contributions.Aggregate(
                CachePolicyFields.None,
                static (mask, contribution) => mask | contribution.Fields);

            return new CacheRuntimePolicy
            {
                MethodId = result.MethodId,
                Duration = result.Policy.Duration,
                Tags = result.Policy.Tags,
                Version = result.Policy.Version,
                RequireIdempotent = result.Policy.RequireIdempotent,
                KeyGeneratorType = result.Policy.KeyGeneratorType,
                SlidingExpiration = runtimeOptions.SlidingExpiration,
                RefreshAhead = runtimeOptions.RefreshAhead,
                StampedeProtection = runtimeOptions.StampedeProtection,
                DistributedLock = runtimeOptions.DistributedLock,
                Metadata = metadata,
                Fields = fields
            };
        }

        /// <summary>
        /// Creates a runtime policy from a cache policy.
        /// </summary>
        public static CacheRuntimePolicy FromPolicy(
            string methodId,
            CachePolicy policy,
            CachePolicyFields fields,
            IReadOnlyDictionary<string, string?>? metadata = null,
            CacheRuntimeOptions? runtimeOptions = null)
        {
            if (string.IsNullOrEmpty(methodId))
                throw new ArgumentException("Method ID is required", nameof(methodId));
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            var metadataDict = metadata != null
                ? new Dictionary<string, string?>(metadata, StringComparer.Ordinal)
                : new Dictionary<string, string?>(StringComparer.Ordinal);

            // Merge policy metadata
            if (policy.Metadata != null)
            {
                foreach (var kvp in policy.Metadata)
                {
                    metadataDict.TryAdd(kvp.Key, kvp.Value);
                }
            }

            var options = runtimeOptions ?? CacheRuntimeOptions.Empty;

            return new CacheRuntimePolicy
            {
                MethodId = methodId,
                Duration = policy.Duration,
                Tags = policy.Tags,
                Version = policy.Version,
                RequireIdempotent = policy.RequireIdempotent,
                KeyGeneratorType = policy.KeyGeneratorType,
                SlidingExpiration = options.SlidingExpiration,
                RefreshAhead = options.RefreshAhead,
                StampedeProtection = options.StampedeProtection,
                DistributedLock = options.DistributedLock,
                Metadata = metadataDict,
                Fields = fields
            };
        }

        /// <summary>
        /// Creates an empty runtime policy for the specified method.
        /// </summary>
        public static CacheRuntimePolicy Empty(string methodId)
        {
            if (string.IsNullOrEmpty(methodId))
                throw new ArgumentException("Method ID is required", nameof(methodId));

            return new CacheRuntimePolicy
            {
                MethodId = methodId,
                Tags = Array.Empty<string>(),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal),
                Fields = CachePolicyFields.None
            };
        }

        // ============= Helper Methods =============

        private static CacheRuntimeOptions ExtractRuntimeOptions(Dictionary<string, string?> metadata)
        {
            // Extract runtime options from metadata if they've been serialized there
            // This supports the case where runtime options flow through the policy pipeline

            TimeSpan? slidingExpiration = null;
            TimeSpan? refreshAhead = null;
            StampedeProtectionOptions? stampedeProtection = null;
            DistributedLockOptions? distributedLock = null;

            if (metadata.TryGetValue("runtime.slidingExpiration", out var slidingStr) &&
                TimeSpan.TryParse(slidingStr, out var sliding))
            {
                slidingExpiration = sliding;
            }

            if (metadata.TryGetValue("runtime.refreshAhead", out var refreshStr) &&
                TimeSpan.TryParse(refreshStr, out var refresh))
            {
                refreshAhead = refresh;
            }

            // Parse stampede protection if present
            if (metadata.TryGetValue("runtime.stampedeProtection.mode", out var modeStr) &&
                Enum.TryParse<StampedeProtectionMode>(modeStr, out var mode))
            {
                var beta = metadata.TryGetValue("runtime.stampedeProtection.beta", out var betaStr) &&
                           double.TryParse(betaStr, out var betaValue) ? betaValue : 1.0;
                var window = metadata.TryGetValue("runtime.stampedeProtection.refreshAheadWindow", out var windowStr) &&
                            TimeSpan.TryParse(windowStr, out var windowValue) ? windowValue : (TimeSpan?)null;

                stampedeProtection = new StampedeProtectionOptions(mode, beta, window);
            }

            // Parse distributed lock if present
            if (metadata.TryGetValue("runtime.distributedLock.timeout", out var timeoutStr) &&
                TimeSpan.TryParse(timeoutStr, out var timeout))
            {
                var maxConcurrency = metadata.TryGetValue("runtime.distributedLock.maxConcurrency", out var maxStr) &&
                                    int.TryParse(maxStr, out var max) ? max : 1;

                distributedLock = new DistributedLockOptions(timeout, maxConcurrency);
            }

            return (slidingExpiration.HasValue || refreshAhead.HasValue ||
                    stampedeProtection != null || distributedLock != null)
                ? new CacheRuntimeOptions
                {
                    SlidingExpiration = slidingExpiration,
                    RefreshAhead = refreshAhead,
                    StampedeProtection = stampedeProtection,
                    DistributedLock = distributedLock
                }
                : CacheRuntimeOptions.Empty;
        }
    }
}