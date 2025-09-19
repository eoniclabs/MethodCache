using System;

namespace MethodCache.Core.Options
{
    /// <summary>
    /// Options controlling streaming cache operations.
    /// </summary>
    public sealed class StreamCacheOptions
    {
        internal StreamCacheOptions(TimeSpan? duration, int segmentSize, int maxMemorySize, bool enableWindowing)
        {
            Duration = duration;
            SegmentSize = segmentSize;
            MaxMemorySize = maxMemorySize;
            EnableWindowing = enableWindowing;
        }

        /// <summary>
        /// Gets the absolute duration applied to cached stream segments.
        /// </summary>
        public TimeSpan? Duration { get; }

        /// <summary>
        /// Gets the preferred segment size used when materialising streaming responses.
        /// </summary>
        public int SegmentSize { get; }

        /// <summary>
        /// Gets the maximum number of items to cache in memory for a stream.
        /// </summary>
        public int MaxMemorySize { get; }

        /// <summary>
        /// Indicates whether windowing is enabled for large streams.
        /// </summary>
        public bool EnableWindowing { get; }

        public sealed class Builder
        {
            private TimeSpan? _duration;
            private int _segmentSize = 256;
            private int _maxMemorySize = 10_000;
            private bool _enableWindowing;

            public Builder WithDuration(TimeSpan duration)
            {
                if (duration <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
                }

                _duration = duration;
                return this;
            }

            public Builder WithSegmentSize(int segmentSize)
            {
                if (segmentSize <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(segmentSize), "Segment size must be positive.");
                }

                _segmentSize = segmentSize;
                return this;
            }

            public Builder WithMaxMemorySize(int maxMemorySize)
            {
                if (maxMemorySize <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxMemorySize), "Max memory size must be positive.");
                }

                _maxMemorySize = maxMemorySize;
                return this;
            }

            public Builder EnableWindowing(bool enabled = true)
            {
                _enableWindowing = enabled;
                return this;
            }

            public StreamCacheOptions Build()
            {
                return new StreamCacheOptions(_duration, _segmentSize, _maxMemorySize, _enableWindowing);
            }
        }
    }
}
