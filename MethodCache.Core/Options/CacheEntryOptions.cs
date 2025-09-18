using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MethodCache.Core.Options
{
    /// <summary>
    /// Immutable options describing how a fluent cache entry should behave.
    /// </summary>
    public sealed class CacheEntryOptions
    {
        internal CacheEntryOptions(TimeSpan? duration, IReadOnlyList<string> tags)
        {
            Duration = duration;
            Tags = tags;
        }

        /// <summary>
        /// Gets the absolute duration for the cache entry.
        /// </summary>
        public TimeSpan? Duration { get; }

        /// <summary>
        /// Gets the tags associated with the entry for downstream invalidation.
        /// </summary>
        public IReadOnlyList<string> Tags { get; }

        /// <summary>
        /// Builder for <see cref="CacheEntryOptions"/> instances.
        /// </summary>
        public sealed class Builder
        {
            private TimeSpan? _duration;
            private readonly List<string> _tags = new();

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
            /// Builds an immutable <see cref="CacheEntryOptions"/> instance.
            /// </summary>
            public CacheEntryOptions Build()
            {
                return new CacheEntryOptions(_duration, new ReadOnlyCollection<string>(_tags));
            }
        }
    }
}
