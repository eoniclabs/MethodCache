using System.Collections.Concurrent;
using MethodCache.Infrastructure;
using MethodCache.Policies;

namespace MethodCache.Core.PolicyPipeline.Resolution;

internal sealed class PolicyRegistry : IPolicyRegistry, IDisposable
{
    private readonly PolicyResolver _resolver;
    private readonly ConcurrentDictionary<string, PolicyHandle> _handles = new(StringComparer.Ordinal);
    private int _disposed;
    private readonly Func<string, Task>? _invalidate;
    private readonly ConcurrentDictionary<string, Task> _invalidations = new(StringComparer.Ordinal);
    internal Task Invalidation(string methodId) => _invalidations.GetValueOrDefault(methodId) ?? Task.CompletedTask;

    public PolicyRegistry(PolicyResolver resolver, Func<string, Task>? invalidate = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _invalidate = invalidate;

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
        OnPolicyChanged(GetPolicy(methodId));
        return handle;
    }

    internal void InvalidateGeneration(string methodId)
    {
        if (_handles.TryGetValue(methodId, out var handle)) handle.InvalidateGeneration();
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
            if (handle.Update(result, awaitInvalidation: _invalidate != null) && _invalidate != null)
            {
                var pending = CompleteInvalidationAsync(result.MethodId, handle.Current.Generation!);
                _invalidations[result.MethodId] = pending;
                _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
    private async Task CompleteInvalidationAsync(string methodId, PolicyGeneration generation)
    {
        await _invalidate!(methodId).ConfigureAwait(false);
        generation.DistributedReady = true;
    }

}
