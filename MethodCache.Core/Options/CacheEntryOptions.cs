using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
            IReadOnlyList<Action<CacheContext>> onMissCallbacks)
        {
            Duration = duration;
            SlidingExpiration = slidingExpiration;
            RefreshAhead = refreshAhead;
            Tags = tags;
            OnHitCallbacks = onHitCallbacks;
            OnMissCallbacks = onMissCallbacks;
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

            /// <summary>
            /// Sets the cache duration.
            /// </summary>
            public Builder WithDuration(TimeSpan duration)
            {
                if (duration <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
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
                    throw new ArgumentOutOfRangeException(nameof(slidingExpiration), "Sliding expiration must be positive.");
                }

                _slidingExpiration = slidingExpiration;
                return this;
            }

            /// <summary>
            /// Requests refresh-ahead behaviour before the entry expires.
            /// </summary>
            public Builder RefreshAhead(TimeSpan window)
            {
                if (window < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(window), "Refresh window must be non-negative.");
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
                    new ReadOnlyCollection<Action<CacheContext>>(_onMissCallbacks));
            }
        }
    }
}
