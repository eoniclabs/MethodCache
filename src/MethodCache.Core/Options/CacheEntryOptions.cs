using MethodCache.Infrastructure;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MethodCache.Extensibility;

namespace MethodCache
{
    /// <summary>
    /// Immutable options describing how a fluent cache entry should behave.
    /// </summary>
    public sealed class CacheEntryOptions
    {
        internal static CacheEntryOptions Default { get; } = new Builder().Build();
        internal CacheEntryOptions(
            TimeSpan? duration,
            TimeSpan? slidingExpiration,
            TimeSpan? refreshAhead,
            IReadOnlyList<string> tags,
            int? version,
            TimeSpan? failSafe,
            TimeSpan? failSafeTimeout,
            TimeSpan? failSafeRetryAfter,
            bool clearsTags = false, bool? enabled = null, bool? distributed = null, Func<object?, bool>? cacheWhen = null)
        {
            Enabled = enabled;
            Distributed = distributed;
            CacheWhen = cacheWhen;
            ClearsTags = clearsTags;
            Duration = duration;
            SlidingExpiration = slidingExpiration;
            RefreshAhead = refreshAhead;
            Tags = tags;
            Version = version;
            FailSafe = failSafe;
            FailSafeTimeout = failSafeTimeout;
            FailSafeRetryAfter = failSafeRetryAfter;
        }

        /// <summary>
        /// Gets the absolute duration for the cache entry.
        /// </summary>
        public TimeSpan? Duration { get; }

        /// <summary>
        /// Gets the sliding expiration window for the cache entry.
        /// </summary>
        public TimeSpan? SlidingExpiration { get; }

        /// <summary>
        /// Gets the refresh-ahead window for the cache entry.
        /// </summary>
        public TimeSpan? RefreshAhead { get; }

        /// <summary>
        /// Gets the tags associated with the entry for downstream invalidation.
        /// </summary>
        public IReadOnlyList<string> Tags { get; }

        /// <summary>
        /// Gets whether <see cref="Builder.ClearTags"/> was called, so the policy of a cached method drops the tags
        /// of its attribute and of the other lower layers instead of adding to them.
        /// </summary>
        public bool ClearsTags { get; }
        /// <summary>Whether caching is enabled; unset inherits the default.</summary>
        public bool? Enabled { get; }
        /// <summary>Whether distributed caching is enabled; unset inherits the default.</summary>
        public bool? Distributed { get; }
        internal Func<object?, bool>? CacheWhen { get; }
        /// <summary>Checks whether the typed result may be cached. A throwing predicate rejects the result.</summary>
        /// <remarks>Custom managers apply this check both before publication and after distributed reads.</remarks>
        public bool CanCache<T>(T value)
        {
            if (Enabled == false) return false;
            try { return CacheWhen?.Invoke(value) != false; }
            catch (Exception) { return false; }
        }
        /// <summary>Returns the canonical key for these options, applying an explicit-key version exactly once.</summary>
        /// <remarks>Custom managers use this before storage; generated calls already carry their complete key.</remarks>
        public string GetCacheKey(string key) => Runtime != null ? key : CacheManagerExtensions.GetCacheKey(key, Version);
        internal CacheRuntimePolicy? Runtime { get; init; }
        internal static CacheEntryOptions FromRuntime(CacheRuntimePolicy policy, bool preparedKey = true) => new(
            policy.Duration, policy.SlidingExpiration, policy.RefreshAhead,
            Array.AsReadOnly(policy.Tags.ToArray()), policy.Version,
            policy.FailSafe, policy.FailSafeTimeout, policy.FailSafeRetryAfter,
            enabled: policy.Enabled, distributed: policy.Distributed, cacheWhen: policy.CacheWhen)
        { Runtime = preparedKey ? policy : null };



        /// <summary>
        /// Gets the version that is added to the key, so changing it skips entries stored under the old one.
        /// </summary>
        public int? Version { get; }

        /// <summary>
        /// Gets how long an expired value is kept as a fail-safe fallback; <see cref="TimeSpan.Zero"/> turns it off.
        /// </summary>
        public TimeSpan? FailSafe { get; }

        /// <summary>
        /// Gets how long a reload may take before the caller gets the fail-safe value instead.
        /// </summary>
        public TimeSpan? FailSafeTimeout { get; }

        /// <summary>
        /// Gets how long a served fail-safe value is reused before the method is called again.
        /// </summary>
        public TimeSpan? FailSafeRetryAfter { get; }

        /// <summary>
        /// Builder for <see cref="CacheEntryOptions"/> instances.
        /// </summary>
        public sealed class Builder
        {
            private bool? _enabled;
            private bool? _distributed;
            private Func<object?, bool>? _cacheWhen;
            /// <summary>Enables or disables all caching for the invocation or method.</summary>
            public Builder WithEnabled(bool enabled) { _enabled = enabled; return this; }
            /// <summary>Enables or disables distributed reads and writes.</summary>
            public Builder WithDistributed(bool distributed) { _distributed = distributed; return this; }
            /// <summary>Stores and accepts only results satisfying the typed predicate.</summary>
            public Builder CacheWhen<T>(Func<T, bool> predicate)
            {
                ArgumentNullException.ThrowIfNull(predicate);
                _cacheWhen = new TypedCachePredicate<T>(predicate).Accept;
                return this;
            }
            private TimeSpan? _duration;
            private TimeSpan? _slidingExpiration;
            private TimeSpan? _refreshAhead;
            private readonly List<string> _tags = new();
            private bool _clearTags;
            private int? _version;
            private TimeSpan? _failSafe;
            private TimeSpan? _failSafeTimeout;
            private TimeSpan? _failSafeRetryAfter;

            /// <summary>
            /// Sets the cache duration.
            /// </summary>
            public Builder WithDuration(TimeSpan duration)
            {
                if (duration <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(duration),
                        $"Cache duration must be positive. Provided: {duration}. " +
                        "Without a duration, entries use MethodCacheMemoryOptions.DefaultExpiration (5 minutes unless configured); " +
                        "no entry lives longer than MethodCacheMemoryOptions.MaxExpiration. " +
                        "Common durations: TimeSpan.FromMinutes(5), TimeSpan.FromHours(1). " +
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/features/expiration.md#duration");
                }

                _duration = duration;
                return this;
            }

            /// <summary>
            /// Adds one or more tags to the cache entry. For a cached method (startup code or a runtime override),
            /// they are added to the tags of its <c>[Cache]</c> attribute and of the other layers, so
            /// <c>[CacheInvalidate]</c> still reaches the entry; call <see cref="ClearTags"/> first to replace them.
            /// </summary>
            public Builder WithTags(params string[] tags)
            {
                if (tags == null)
                {
                    throw new ArgumentNullException(nameof(tags));
                }

                foreach (var tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        _tags.Add(tag);
                    }
                }

                return this;
            }

            /// <summary>
            /// Removes the tags added so far. For a cached method, it also drops the tags of its <c>[Cache]</c>
            /// attribute, of appsettings and of the defaults, so only tags added after this call apply. An entry
            /// without those tags is no longer removed by a <c>[CacheInvalidate]</c> that lists them.
            /// </summary>
            public Builder ClearTags()
            {
                _tags.Clear();
                _clearTags = true;
                return this;
            }

            /// <summary>
            /// Sets the sliding expiration window for the cache entry.
            /// </summary>
            public Builder WithSlidingExpiration(TimeSpan slidingExpiration)
            {
                if (slidingExpiration <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(slidingExpiration),
                        $"Sliding expiration must be positive. Provided: {slidingExpiration}. " +
                        "Sliding expiration resets the timer on each cache access. " +
                        "Example: TimeSpan.FromMinutes(10) keeps frequently-accessed items cached. " +
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/features/expiration.md#sliding-expiration");
                }

                _slidingExpiration = slidingExpiration;
                return this;
            }

            /// <summary>
            /// Requests refresh-ahead behaviour before the entry expires.
            /// </summary>
            public Builder RefreshAhead(TimeSpan window)
            {
                if (window <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(window), "Refresh window must be positive.");
                }

                _refreshAhead = window;
                return this;
            }

            /// <summary>
            /// Keeps expired values for <paramref name="duration"/> and returns them when reloading throws, so a failing
            /// dependency degrades to slightly stale data instead of errors. With <paramref name="timeout"/>, a reload
            /// that takes longer also returns the old value and finishes in the background. After a fallback the old
            /// value is reused for <paramref name="retryAfter"/> (default 30 seconds) before the method is tried again.
            /// <see cref="TimeSpan.Zero"/> as <paramref name="duration"/> turns fail-safe off, for example for one
            /// method when it is on for all of them.
            /// </summary>
            public Builder FailSafe(TimeSpan duration, TimeSpan? timeout = null, TimeSpan? retryAfter = null)
            {
                if (duration < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(duration), duration, "Fail-safe duration cannot be negative; use TimeSpan.Zero to turn fail-safe off.");
                }

                if (timeout <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Fail-safe timeout must be positive.");
                }

                if (retryAfter <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "Fail-safe retry interval must be positive.");
                }

                _failSafe = duration;
                _failSafeTimeout = timeout ?? _failSafeTimeout;
                _failSafeRetryAfter = retryAfter ?? _failSafeRetryAfter;
                return this;
            }

            /// <summary>
            /// Sets a version that becomes part of the key, so entries cached under an older version are ignored.
            /// </summary>
            public Builder WithVersion(int version)
            {
                if (version < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(version), "Version must be non-negative.");
                }

                _version = version;
                return this;
            }

            /// <summary>
            /// Builds an immutable <see cref="CacheEntryOptions"/> instance.
            /// </summary>
            /// <exception cref="InvalidOperationException">The refresh window is not shorter than the duration or the sliding expiration.</exception>
            public CacheEntryOptions Build()
            {
                // A window as long as the entry lives would reload the value on every hit
                if (_refreshAhead >= _duration || _refreshAhead >= _slidingExpiration)
                {
                    var lifetime = _refreshAhead >= _duration ? $"Duration ({_duration})" : $"SlidingExpiration ({_slidingExpiration})";
                    throw new InvalidOperationException(
                        $"RefreshAhead ({_refreshAhead}) must be shorter than {lifetime}, or every hit would reload the value. " +
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/features/expiration.md#refresh-ahead");
                }

                return new CacheEntryOptions(
                    _duration,
                    _slidingExpiration,
                    _refreshAhead,
                    new ReadOnlyCollection<string>(_tags.ToArray()),
                    _version,
                    _failSafe,
                    _failSafeTimeout,
                    _failSafeRetryAfter,
                    _clearTags, _enabled, _distributed, _cacheWhen);
            }
        }
    }
}
