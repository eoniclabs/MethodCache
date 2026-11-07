using System.Collections.Concurrent;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.PolicyPipeline.Model;
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
    internal void ForgetInvalidation(string methodId, Task completed)
        => _invalidations.TryRemove(new KeyValuePair<string, Task>(methodId, completed));

    internal PolicyResolver Resolver => _resolver;

    // Overrides that every instance applies, started before the first policy is served
    private readonly Func<SharedRuntimeOverrides?>? _shared;
    private readonly object _sharedSync = new();
    private volatile bool _sharedStarted;

    /// <summary>
    /// Whether runtime overrides apply on every instance, so entries cached under one are shared like any other.
    /// Known once a policy was served.
    /// </summary>
    internal bool SharesOverrides { get; private set; }

    public PolicyRegistry(PolicyResolver resolver, Func<string, Task>? invalidate = null, Func<SharedRuntimeOverrides?>? shared = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _invalidate = invalidate;
        _shared = shared;

        // Subscribed before any handle exists, so no change can slip between creating a handle and watching it
        _resolver.Changed += OnPolicyChanged;
    }

    public PolicyResolutionResult GetPolicy(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        EnsureSharedOverrides(wait: false);
        // Completes synchronously once the sources are loaded, which happens on the first call
        return _resolver.ResolveAsync(methodId).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies()
    {
        EnsureSharedOverrides(wait: false);
        return _resolver.GetAllAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reads the overrides every instance applies from the distributed cache and waits for them, so an instance that
    /// starts with a generic host serves its first call with them. Called when the host starts.
    /// </summary>
    internal void StartSharedOverrides() => EnsureSharedOverrides(wait: true);

    /// <summary>
    /// Starts reading the overrides every instance applies when the first policy is read. Without a host, a first call
    /// doesn't wait for them: the distributed cache never holds up a call.
    /// </summary>
    private void EnsureSharedOverrides(bool wait)
    {
        if (_sharedStarted || _shared == null || Monitor.IsEntered(_sharedSync)) return;
        lock (_sharedSync)
        {
            if (_sharedStarted) return;
            if (_shared() is { Enabled: true } shared)
            {
                var cached = _resolver.GetAllAsync().ConfigureAwait(false).GetAwaiter().GetResult()
                    .Where(static policy => policy.Contributions.Any(static contribution => contribution.SourceId == PolicySourceIds.Attributes))
                    .Select(static policy => policy.MethodId)
                    .ToHashSet(StringComparer.Ordinal);
                SharesOverrides = true;
                shared.Start(cached, _resolver, wait);
            }

            _sharedStarted = true;
        }
    }

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

        handle = _handles.GetOrAdd(methodId, static (id, registry) =>
        {
            var policy = registry.GetPolicy(id);
            return new PolicyHandle(policy, registry.SharesOverrides);
        }, this);

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
            if (handle.Update(result, awaitInvalidation: _invalidate != null, SharesOverrides) && _invalidate != null)
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
