using System;

namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Options for the distributed cache (L2) that MethodCache uses behind its in-memory cache.
    /// </summary>
    public class DistributedCacheOptions
    {
        private string _keyPrefix = "methodcache:";
        private TimeSpan _tagCheckInterval = TimeSpan.FromSeconds(1);
        private TimeSpan _tagRetention = TimeSpan.FromDays(1);

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
        /// the distributed cache, so an invalidation always outlives the entries it applies to.
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
    }
}
