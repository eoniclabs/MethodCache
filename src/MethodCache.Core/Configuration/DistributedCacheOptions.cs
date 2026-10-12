using System;

namespace MethodCache
{
    /// <summary>
    /// Options for the distributed cache (L2) that MethodCache uses behind its in-memory cache.
    /// </summary>
    public class DistributedCacheOptions
    {
        private string _keyPrefix = "methodcache:";
        private TimeSpan _tagCheckInterval = TimeSpan.FromSeconds(1);
        private TimeSpan _tagRetention = TimeSpan.FromDays(1);
        private TimeSpan _operationTimeout = TimeSpan.FromSeconds(1);
        private TimeSpan _circuitBreakerDuration = TimeSpan.FromSeconds(10);
        private TimeSpan _clockSkewTolerance = TimeSpan.FromSeconds(2);

        internal bool HasEffectiveTagRetention => TagRetention.Ticks / TimeSpan.TicksPerMillisecond
            > ClockSkewTolerance.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>
        /// Prefix for every key MethodCache writes to the distributed cache, so several applications can share it.
        /// </summary>
        public string KeyPrefix
        {
            get => _keyPrefix;
            set => _keyPrefix = value ?? throw new ArgumentNullException(nameof(KeyPrefix));
        }

        /// <summary>
        /// How long a node trusts the invalidation time it last read for a tag before reading it again. Tag
        /// invalidations sent through a backplane reach other nodes immediately; without one, entries read from
        /// the distributed cache can outlive an invalidation on another node by up to this long.
        /// </summary>
        public TimeSpan TagCheckInterval
        {
            get => _tagCheckInterval;
            set
            {
                if (value < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(TagCheckInterval), value, "TagCheckInterval cannot be negative.");
                _tagCheckInterval = value;
            }
        }

        /// <summary>
        /// How long tag invalidations are kept in the distributed cache. Entries never live longer than this in
        /// the distributed cache minus ClockSkewTolerance, so an invalidation always outlives the entries it applies to.
        /// Must exceed ClockSkewTolerance after both are truncated to whole milliseconds; registration validates this at startup.
        /// </summary>
        public TimeSpan TagRetention
        {
            get => _tagRetention;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(TagRetention), value, "TagRetention must be greater than zero.");
                _tagRetention = value;
            }
        }

        /// <summary>
        /// The longest a single distributed cache operation may take. A slower one is abandoned and counts as a
        /// failure, so a hanging Redis or SQL Server never holds up a call for longer than this. Default 1 second.
        /// </summary>
        public TimeSpan OperationTimeout
        {
            get => _operationTimeout;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(OperationTimeout), value, "OperationTimeout must be greater than zero.");
                _operationTimeout = value;
            }
        }

        /// <summary>
        /// How long MethodCache uses only its in-memory cache after a distributed cache operation fails or times
        /// out. After that, one call tries the distributed cache again. Invalidations made meanwhile are retried at
        /// this interval. Default 10 seconds.
        /// </summary>
        public TimeSpan CircuitBreakerDuration
        {
            get => _circuitBreakerDuration;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(CircuitBreakerDuration), value, "CircuitBreakerDuration must be greater than zero.");
                _circuitBreakerDuration = value;
            }
        }

        /// <summary>
        /// How far apart the clocks of your servers may be. A tag invalidation also applies to entries another
        /// server created up to this long after it, so a server whose clock runs ahead can't keep a stale entry
        /// alive. Default 2 seconds.
        /// </summary>
        public TimeSpan ClockSkewTolerance
        {
            get => _clockSkewTolerance;
            set
            {
                if (value < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(ClockSkewTolerance), value, "ClockSkewTolerance cannot be negative.");
                _clockSkewTolerance = value;
            }
        }
    }
}
