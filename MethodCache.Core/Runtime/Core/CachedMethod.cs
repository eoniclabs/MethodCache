using MethodCache.Core.Runtime.KeyGeneration;

namespace MethodCache.Core.Runtime.Core
{
    /// <summary>
    /// One cached method as its generated decorator sees it: the name it reports, its live policy and the key
    /// prefix that policy implies. Decorators read <see cref="Current"/> once per call; the prefix is only rebuilt
    /// when the policy changes, for example when a runtime override bumps the version.
    /// </summary>
    public sealed class CachedMethod
    {
        private readonly PolicyHandle _handle;
        private readonly Type _serviceType;
        private readonly string _keyMethodName;
        private Snapshot _snapshot;

        /// <param name="handle">The method's shared policy handle.</param>
        /// <param name="serviceType">The service type that starts the method's keys.</param>
        /// <param name="methodName">The method's name in its keys.</param>
        /// <param name="displayName">The name reported to the cache manager and metrics; defaults to <paramref name="methodName"/>.</param>
        public CachedMethod(PolicyHandle handle, Type serviceType, string methodName, string? displayName = null)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            _keyMethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
            Name = displayName ?? methodName;
            KeyMethod = MethodCacheKeys.Prefix(serviceType, methodName);
            _snapshot = CreateSnapshot(handle.Current);
        }

        /// <summary>
        /// The name reported to the cache manager and metrics.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The key prefix without a version, passed to key generators, which add the version themselves.
        /// </summary>
        public string KeyMethod { get; }

        /// <summary>
        /// The current policy and the key prefix that belongs to it.
        /// </summary>
        public Snapshot Current
        {
            get
            {
                var policy = _handle.Current;
                var snapshot = Volatile.Read(ref _snapshot);
                if (ReferenceEquals(snapshot.Policy, policy))
                {
                    return snapshot;
                }

                // Racing callers may each build a snapshot for the new policy; they are equal, so either one may win
                snapshot = CreateSnapshot(policy);
                Volatile.Write(ref _snapshot, snapshot);
                return snapshot;
            }
        }

        private Snapshot CreateSnapshot(CacheRuntimePolicy policy)
            => new(policy, MethodCacheKeys.Prefix(_serviceType, _keyMethodName, policy.Version));

        /// <summary>
        /// A policy together with the key prefix it implies.
        /// </summary>
        public sealed class Snapshot
        {
            internal Snapshot(CacheRuntimePolicy policy, string keyPrefix)
            {
                Policy = policy;
                KeyPrefix = keyPrefix;
            }

            /// <summary>
            /// The policy to cache with.
            /// </summary>
            public CacheRuntimePolicy Policy { get; }

            /// <summary>
            /// The service type and method name, plus the policy version when it has one. For methods without
            /// arguments this is the whole key.
            /// </summary>
            public string KeyPrefix { get; }
        }
    }
}
