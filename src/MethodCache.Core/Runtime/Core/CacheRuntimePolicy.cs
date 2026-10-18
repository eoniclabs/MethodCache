using MethodCache.Policies;

namespace MethodCache.Infrastructure
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
    internal sealed record CacheRuntimePolicy
    {
        internal bool CanonicalTagParts { get; init; } = true;
        internal PolicyGeneration? Generation { get; init; }
        internal string? Identity { get; init; }
        internal bool PrivateIdentity { get; init; }
        private readonly IReadOnlyCollection<string> _tags = Array.Empty<string>();
        /// <summary>
        /// The unique identifier of the method this policy applies to.
        /// </summary>
        public required string MethodId { get; init; }

        // ============= Core Policy Data (from CachePolicy) =============

        /// <summary>
        /// The duration for which cached values should be retained.
        /// </summary>
        public TimeSpan? Duration { get; init; }

        /// <summary>Whether caching is enabled; unset inherits defaults.</summary>
        public bool? Enabled { get; init; }

        /// <summary>Whether this policy uses distributed storage; unset inherits defaults.</summary>
        public bool? Distributed { get; init; }

        /// <summary>
        /// Tags associated with cache entries for bulk invalidation.
        /// </summary>
        public IReadOnlyCollection<string> Tags
        {
            get => _tags;
            init
            {
                ArgumentNullException.ThrowIfNull(value);
                _tags = value.Count == 0 ? Array.Empty<string>() : Array.AsReadOnly(value.ToArray());
            }
        }

        /// <summary>
        /// Version number for cache key generation to support cache busting.
        /// </summary>
        public int? Version { get; init; }

        /// <summary>
        /// Sliding expiration window: each read extends the entry by this much, up to <see cref="Duration"/>.
        /// </summary>
        public TimeSpan? SlidingExpiration { get; init; }

        /// <summary>
        /// Time window before expiration in which a read refreshes the entry in the background.
        /// </summary>
        public TimeSpan? RefreshAhead { get; init; }

        /// <summary>
        /// Decides whether a freshly loaded result is stored. Null stores every result.
        /// </summary>
        public Func<object?, bool>? CacheWhen { get; init; }

        /// <summary>How long an expired value is kept as a fail-safe fallback; zero or null turns fail-safe off.</summary>
        public TimeSpan? FailSafe { get; init; }

        /// <summary>How long a reload may take before the caller gets the fail-safe value instead.</summary>
        public TimeSpan? FailSafeTimeout { get; init; }

        /// <summary>How long a served fail-safe value is reused before the method is called again.</summary>
        public TimeSpan? FailSafeRetryAfter { get; init; }

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
        /// Indicates whether sliding expiration or refresh-ahead is configured.
        /// </summary>
        public bool HasRuntimeOptions =>
            SlidingExpiration.HasValue ||
            RefreshAhead.HasValue;

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

            // Compute fields from contributions
            var fields = result.Contributions.Aggregate(
                CachePolicyFields.None,
                static (mask, contribution) => mask | contribution.Fields);

            return new CacheRuntimePolicy
            {
                MethodId = result.MethodId,
                Duration = result.Policy.Duration,
                Enabled = result.Policy.Enabled,
                Distributed = result.Policy.Distributed,
                CanonicalTagParts = result.Policy.CanonicalTagParts,
                Tags = result.Policy.Tags,
                Version = result.Policy.Version,
                SlidingExpiration = result.Policy.SlidingExpiration,
                RefreshAhead = result.Policy.RefreshAhead,
                CacheWhen = result.Policy.CacheWhen,
                FailSafe = result.Policy.FailSafe,
                FailSafeTimeout = result.Policy.FailSafeTimeout,
                FailSafeRetryAfter = result.Policy.FailSafeRetryAfter,
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
            IReadOnlyDictionary<string, string?>? metadata = null)
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

            return new CacheRuntimePolicy
            {
                MethodId = methodId,
                Duration = policy.Duration,
                Enabled = policy.Enabled,
                Distributed = policy.Distributed,
                CanonicalTagParts = policy.CanonicalTagParts,
                Tags = policy.Tags,
                Version = policy.Version,
                SlidingExpiration = policy.SlidingExpiration,
                RefreshAhead = policy.RefreshAhead,
                CacheWhen = policy.CacheWhen,
                FailSafe = policy.FailSafe,
                FailSafeTimeout = policy.FailSafeTimeout,
                FailSafeRetryAfter = policy.FailSafeRetryAfter,
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
    }
}
