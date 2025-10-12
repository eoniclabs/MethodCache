using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MethodCache.Core.Infrastructure.Metrics;
using MethodCache.Core.Runtime;

namespace MethodCache.Core.Options
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
            StampedeProtectionOptions? stampedeProtection,
            DistributedLockOptions? distributedLock,
            ICacheMetrics? metrics,
            int? version,
            Type? keyGeneratorType,
            Func<CacheContext, bool>? predicate)
        {
            Duration = duration;
            SlidingExpiration = slidingExpiration;
            RefreshAhead = refreshAhead;
            Tags = tags;
            OnHitCallbacks = onHitCallbacks;
            OnMissCallbacks = onMissCallbacks;
            StampedeProtection = stampedeProtection;
            DistributedLock = distributedLock;
            Metrics = metrics;
            Version = version;
            KeyGeneratorType = keyGeneratorType;
            Predicate = predicate;
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
        public StampedeProtectionOptions? StampedeProtection { get; }
        public DistributedLockOptions? DistributedLock { get; }
        public ICacheMetrics? Metrics { get; }
        public int? Version { get; }
        public Type? KeyGeneratorType { get; }
        public Func<CacheContext, bool>? Predicate { get; }

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
            private StampedeProtectionOptions? _stampedeProtection;
            private DistributedLockOptions? _distributedLock;
            private ICacheMetrics? _metrics;
            private int? _version;
            private Type? _keyGeneratorType;
            private Func<CacheContext, bool>? _predicate;

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
                        "For no expiration, omit the Duration property or use TimeSpan.MaxValue. " +
                        "Common durations: TimeSpan.FromMinutes(5), TimeSpan.FromHours(1). " +
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/CONFIGURATION_GUIDE.md#duration");
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
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/CONFIGURATION_GUIDE.md#sliding-expiration");
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

            public Builder WithStampedeProtection(StampedeProtectionOptions options)
            {
                _stampedeProtection = options ?? throw new ArgumentNullException(nameof(options));
                return this;
            }

            public Builder WithStampedeProtection(StampedeProtectionMode mode = StampedeProtectionMode.Probabilistic, double beta = 1.0, TimeSpan? refreshAheadWindow = null)
            {
                if (beta <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(beta), "Beta must be positive.");
                }
                if (refreshAheadWindow.HasValue && refreshAheadWindow.Value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(refreshAheadWindow), "Refresh ahead window must be positive.");
                }

                _stampedeProtection = new StampedeProtectionOptions(mode, beta, refreshAheadWindow);
                return this;
            }

            public Builder WithDistributedLock(TimeSpan timeout, int maxConcurrency = 1)
            {
                if (timeout <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
                }
                if (maxConcurrency <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be positive.");
                }

                _distributedLock = new DistributedLockOptions(timeout, maxConcurrency);
                return this;
            }

            public Builder WithMetrics(ICacheMetrics metrics)
            {
                _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
                return this;
            }

            public Builder WithVersion(int version)
            {
                if (version < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(version), "Version must be non-negative.");
                }

                _version = version;
                return this;
            }

            public Builder WithKeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new()
            {
                _keyGeneratorType = typeof(TGenerator);
                return this;
            }

            internal Builder WithKeyGenerator(Type generatorType)
            {
                _keyGeneratorType = generatorType ?? throw new ArgumentNullException(nameof(generatorType));
                return this;
            }

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
            public CacheEntryOptions Build()
            {
                return new CacheEntryOptions(
                    _duration,
                    _slidingExpiration,
                    _refreshAhead,
                    new ReadOnlyCollection<string>(_tags),
                    new ReadOnlyCollection<Action<CacheContext>>(_onHitCallbacks),
                    new ReadOnlyCollection<Action<CacheContext>>(_onMissCallbacks),
                    _stampedeProtection,
                    _distributedLock,
                    _metrics,
                    _version,
                    _keyGeneratorType,
                    _predicate);
            }
        }
    }
}
