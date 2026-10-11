using System.Collections.Concurrent;
using MethodCache.Abstractions.Registry;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Runtime.Core;

namespace MethodCache.Core.PolicyPipeline.Resolution;

internal sealed class PolicyRegistry : IPolicyRegistry, IDisposable
{
    private readonly PolicyResolver _resolver;
    private readonly ConcurrentDictionary<string, PolicyHandle> _handles = new(StringComparer.Ordinal);
    private int _disposed;

    public PolicyRegistry(PolicyResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

        // Subscribed before any handle exists, so no change can slip between creating a handle and watching it
        _resolver.Changed += OnPolicyChanged;
    }

    public PolicyResolutionResult GetPolicy(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        // Completes synchronously once the sources are loaded, which happens on the first call
        return _resolver.ResolveAsync(methodId).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies()
        => _resolver.GetAllAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    public IAsyncEnumerable<PolicyResolutionResult> WatchAsync(CancellationToken cancellationToken = default)
        => _resolver.WatchAsync(null, cancellationToken);

    public long GetPolicyVersion(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            return 0;
        }

        return GetPolicy(methodId).Version;
    }

    /// <summary>
    /// Returns the method's shared handle, which follows every later change to its policy.
    /// </summary>
    public PolicyHandle GetHandle(string methodId)
    {
        if (_handles.TryGetValue(methodId, out var handle))
        {
            return handle;
        }

        handle = _handles.GetOrAdd(methodId, static (id, registry) => new PolicyHandle(registry.GetPolicy(id)), this);

        // A change raised after the handle's policy was resolved but before the handle was published went
        // unseen; re-reading is safe because the handle ignores versions it already has
        handle.Update(GetPolicy(methodId));
        return handle;
    }

    public void Dispose()
    {
        // The instance is registered under both its concrete type and its interface,
        // so the container disposes it once per registration
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _resolver.Changed -= OnPolicyChanged;
    }

    private void OnPolicyChanged(PolicyResolutionResult result)
    {
        if (_handles.TryGetValue(result.MethodId, out var handle))
        {
            handle.Update(result);
        }
    }
}
