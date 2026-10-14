using System.ComponentModel;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Policies;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// The live policy of one cached method. Every decorator for the method shares the same handle and reads
    /// <see cref="Current"/> on each call, so a runtime override or a configuration reload reaches services that
    /// were already created, at the cost of one volatile read.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class PolicyHandle
    {
        private readonly object _sync = new();
        private CacheRuntimePolicy _current;
        private long _version;

        internal PolicyHandle(PolicyResolutionResult result)
        {
            MethodId = result.MethodId;
            _current = CacheRuntimePolicy.FromResolverResult(result);
            _version = result.Version;
        }

        private PolicyHandle(CacheRuntimePolicy policy)
        {
            MethodId = policy.MethodId;
            _current = policy;
        }

        // The handle of one registration of an interface: the method's shared handle, plus what the registration adds
        private PolicyHandle(PolicyHandle shared, RegistrationPolicyRegistry registration)
        {
            MethodId = shared.MethodId;
            _current = shared.Current;
            Shared = shared;
            KeySuffix = registration.KeySuffix;
            Background = registration.Background;
        }

        /// <summary>For the handle of one registration, the method's shared handle; otherwise null.</summary>
        internal PolicyHandle? Shared { get; }

        /// <summary>The service key part of a keyed registration's cache keys (<c>@ecb</c>), else null.</summary>
        internal string? KeySuffix { get; }

        /// <summary>How a background load gets its own instance of a scoped or transient registration, else null.</summary>
        internal BackgroundLoadTarget? Background { get; }

        /// <summary>
        /// The method the policy applies to.
        /// </summary>
        public string MethodId { get; }

        /// <summary>
        /// The method's policy as it is right now.
        /// </summary>
        public CacheRuntimePolicy Current => Shared?.Current ?? Volatile.Read(ref _current);

        /// <summary>
        /// Returns the shared handle for a method. Registries other than MethodCache's own cannot report changes,
        /// so for those the handle keeps the policy the registry returns now.
        /// </summary>
        public static PolicyHandle For(IPolicyRegistry registry, string methodId)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            if (registry is RegistrationPolicyRegistry registration)
            {
                return new PolicyHandle(For(registration.Inner, methodId), registration);
            }

            return registry is PolicyRegistry policyRegistry
                ? policyRegistry.GetHandle(methodId)
                : new PolicyHandle(registry.GetPolicy(methodId));
        }

        /// <summary>
        /// Creates a handle that always returns the same policy, for tests and hand-written decorators.
        /// </summary>
        public static PolicyHandle Fixed(CacheRuntimePolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            return new PolicyHandle(policy);
        }

        /// <summary>
        /// Publishes a newly resolved policy unless the handle already holds the same or a newer version.
        /// </summary>
        internal void Update(PolicyResolutionResult result)
        {
            lock (_sync)
            {
                if (result.Version <= _version)
                {
                    return;
                }

                _version = result.Version;
                Volatile.Write(ref _current, CacheRuntimePolicy.FromResolverResult(result));
            }
        }
    }
}
