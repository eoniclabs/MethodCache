using MethodCache.Policies;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// The policy registry the decorators of one registration see: the container's registry, plus what that
    /// registration changes about caching (the service key in its keys, a new scope for background loads).
    /// <see cref="PolicyHandle.For"/> passes these on to the <see cref="CachedMethod"/> instances of the registration.
    /// </summary>
    internal sealed class RegistrationPolicyRegistry(IPolicyRegistry inner, string? keySuffix, BackgroundLoadTarget? background) : IPolicyRegistry
    {
        public IPolicyRegistry Inner { get; } = inner;

        public string? KeySuffix { get; } = keySuffix;

        public BackgroundLoadTarget? Background { get; } = background;

        public PolicyResolutionResult GetPolicy(string methodId) => Inner.GetPolicy(methodId);

        public IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies() => Inner.GetAllPolicies();

        public IAsyncEnumerable<PolicyResolutionResult> WatchAsync(CancellationToken cancellationToken = default) => Inner.WatchAsync(cancellationToken);

        public long GetPolicyVersion(string methodId) => Inner.GetPolicyVersion(methodId);
    }
}
