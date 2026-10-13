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
        internal CacheEntryOptions(
            TimeSpan? duration,
            TimeSpan? slidingExpiration,
            TimeSpan? refreshAhead,
            IReadOnlyList<string> tags,
            IReadOnlyList<Action<CacheContext>> onHitCallbacks,
            IReadOnlyList<Action<CacheContext>> onMissCallbacks,
            ICacheMetrics? metrics,
            int? version,
            Func<CacheContext, bool>? predicate,
            TimeSpan? failSafe,
            TimeSpan? failSafeTimeout,
            TimeSpan? failSafeRetryAfter)
        {
            Duration = duration;
            SlidingExpiration = slidingExpiration;
            RefreshAhead = refreshAhead;
            Tags = tags;
            OnHitCallbacks = onHitCallbacks;
            OnMissCallbacks = onMissCallbacks;
            Metrics = metrics;
            Version = version;
            Predicate = predicate;
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
        /// Gets callbacks executed when the cache entry is served from cache.
        /// </summary>
        public IReadOnlyList<Action<CacheContext>> OnHitCallbacks { get; }

        /// <summary>
        /// Gets callbacks executed when the cache entry is resolved from the factory.
        /// </summary>
        public IReadOnlyList<Action<CacheContext>> OnMissCallbacks { get; }
        /// <summary>
        /// Gets the metrics sink for this call.
        /// </summary>
        public ICacheMetrics? Metrics { get; }

        /// <summary>
        /// Gets the version that is added to the key, so changing it skips entries stored under the old one.
        /// </summary>
        public int? Version { get; }

        /// <summary>
        /// Gets the predicate that decides whether a result is cached.
        /// </summary>
        public Func<CacheContext, bool>? Predicate { get; }

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
            private TimeSpan? _duration;
            private TimeSpan? _slidingExpiration;
            private TimeSpan? _refreshAhead;
            private readonly List<string> _tags = new();
            private readonly List<Action<CacheContext>> _onHitCallbacks = new();
            private readonly List<Action<CacheContext>> _onMissCallbacks = new();
            private ICacheMetrics? _metrics;
            private int? _version;
            private Func<CacheContext, bool>? _predicate;
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
            /// Adds one or more tags to the cache entry.
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
            /// Adds a callback executed when the entry is served from cache.
            /// </summary>
            public Builder OnHit(Action<CacheContext> callback)
            {
                if (callback == null) throw new ArgumentNullException(nameof(callback));
                _onHitCallbacks.Add(callback);
                return this;
            }

            /// <summary>
            /// Adds a callback executed when the entry is created via the factory.
            /// </summary>
            public Builder OnMiss(Action<CacheContext> callback)
            {
                if (callback == null) throw new ArgumentNullException(nameof(callback));
                _onMissCallbacks.Add(callback);
                return this;
            }

            /// <summary>
            /// Attaches an observer that is told about this call's hit, miss or error. Applies to <c>GetOrCreateAsync</c> only;
            /// configuring it for a cached method throws at startup.
            /// </summary>
            public Builder WithMetrics(ICacheMetrics metrics)
            {
                _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
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
            /// Skips the cache for this <c>GetOrCreateAsync</c> call when <paramref name="predicate"/> returns false: the factory
            /// runs and its result is neither read from nor stored in the cache. Applies to <c>GetOrCreateAsync</c> only.
            /// </summary>
            public Builder When(Func<CacheContext, bool> predicate)
            {
                _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
                return this;
            }

            internal Builder WithPredicate(Func<CacheContext, bool>? predicate)
            {
                _predicate = predicate;
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
                    new ReadOnlyCollection<Action<CacheContext>>(_onHitCallbacks.ToArray()),
                    new ReadOnlyCollection<Action<CacheContext>>(_onMissCallbacks.ToArray()),
                    _metrics,
                    _version,
                    _predicate,
                    _failSafe,
                    _failSafeTimeout,
                    _failSafeRetryAfter);
            }
        }
    }
}
