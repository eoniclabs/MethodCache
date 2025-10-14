using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MethodCache.Abstractions.Registry;
using MethodCache.Abstractions.Resolution;

namespace MethodCache.Core.PolicyPipeline.Resolution;

internal sealed class PolicyRegistry : IPolicyRegistry, IAsyncDisposable
{
    private readonly PolicyResolver _resolver;
    private readonly IReadOnlyList<PolicySourceRegistration> _registrations;
    private readonly ConcurrentDictionary<string, PolicyResolutionResult> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownMethodIds = new(StringComparer.Ordinal);
    private readonly object _initializationLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _watchTask;
    private Task? _initializationTask;

    public PolicyRegistry(PolicyResolver resolver, IEnumerable<PolicySourceRegistration> registrations)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        if (registrations == null)
        {
            throw new ArgumentNullException(nameof(registrations));
        }

        _registrations = registrations.ToList();
    }

    public PolicyResolutionResult GetPolicy(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        EnsureInitializedAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        if (_cache.TryGetValue(methodId, out var result))
        {
            return result;
        }

        var resolved = _resolver.ResolveAsync(methodId).ConfigureAwait(false).GetAwaiter().GetResult();
        _cache[methodId] = resolved;
        lock (_knownMethodIds)
        {
            _knownMethodIds.Add(methodId);
        }

        return resolved;
    }

    public IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies()
    {
        EnsureInitializedAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        return _cache.Values.ToList();
    }

    public IAsyncEnumerable<PolicyResolutionResult> WatchAsync(CancellationToken cancellationToken = default)
    {
        return WatchInternalAsync(cancellationToken);
    }

    public long GetPolicyVersion(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            return 0;
        }

        // Fast path: Just read the version from the cached policy
        // This is a single volatile read, extremely cheap (~1-2ns)
        return _cache.TryGetValue(methodId, out var result) ? result.Version : 0;
    }

    public async ValueTask DisposeAsync()
    {
        var initTask = Volatile.Read(ref _initializationTask);
        if (initTask != null)
        {
            try
            {
                await initTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore initialization failures during disposal
            }
        }

        _cts.Cancel();
        if (_watchTask != null)
        {
            try
            {
                await _watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
        }

        _cts.Dispose();
    }

    private Task EnsureInitializedAsync()
    {
        var existing = Volatile.Read(ref _initializationTask);
        if (existing != null)
        {
            return existing;
        }

        lock (_initializationLock)
        {
            existing = _initializationTask;
            if (existing != null)
            {
                return existing;
            }

            existing = InitializeAsync();
            _initializationTask = existing;
        }

        return existing;
    }

    private async Task InitializeAsync()
    {
        foreach (var registration in _registrations)
        {
            var snapshots = await registration.Source.GetSnapshotAsync().ConfigureAwait(false);
            foreach (var snapshot in snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.MethodId))
                {
                    continue;
                }

                lock (_knownMethodIds)
                {
                    _knownMethodIds.Add(snapshot.MethodId);
                }

                var resolved = await _resolver.ResolveAsync(snapshot.MethodId).ConfigureAwait(false);
                _cache[snapshot.MethodId] = resolved;
            }
        }

        _watchTask = Task.Run(() => WatchLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task WatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in _resolver.WatchAsync(null, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _cache[result.MethodId] = result;
                lock (_knownMethodIds)
                {
                    _knownMethodIds.Add(result.MethodId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    private async IAsyncEnumerable<PolicyResolutionResult> WatchInternalAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        await foreach (var result in _resolver.WatchAsync(null, cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }
    }
}
