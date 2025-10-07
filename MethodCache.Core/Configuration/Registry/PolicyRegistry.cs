using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Registry;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration.Resolver;

namespace MethodCache.Core.Configuration.Registry;

internal sealed class PolicyRegistry : IPolicyRegistry, IAsyncDisposable
{
    private readonly PolicyResolver _resolver;
    private readonly IReadOnlyList<PolicySourceRegistration> _registrations;
    private readonly ConcurrentDictionary<string, PolicyResolutionResult> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownMethodIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _watchTask;
    private bool _initialized;

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

        EnsureInitialized();

        if (_cache.TryGetValue(methodId, out var result))
        {
            return result;
        }

        var resolved = _resolver.ResolveAsync(methodId).GetAwaiter().GetResult();
        _cache[methodId] = resolved;
        lock (_knownMethodIds)
        {
            _knownMethodIds.Add(methodId);
        }

        return resolved;
    }

    public IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies()
    {
        EnsureInitialized();
        return _cache.Values.ToList();
    }

    public IAsyncEnumerable<PolicyResolutionResult> WatchAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _resolver.WatchAsync(null, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
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
        _initializationGate.Dispose();
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initializationGate.Wait();
        try
        {
            if (_initialized)
            {
                return;
            }

            foreach (var registration in _registrations)
            {
                var snapshots = registration.Source.GetSnapshotAsync().GetAwaiter().GetResult();
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

                    var resolved = _resolver.ResolveAsync(snapshot.MethodId).GetAwaiter().GetResult();
                    _cache[snapshot.MethodId] = resolved;
                }
            }

            _watchTask = Task.Run(() => WatchLoopAsync(_cts.Token));
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
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
}
