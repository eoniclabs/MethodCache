using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Resolver;

internal sealed class PolicyResolver : IPolicyResolver, IAsyncDisposable
{
    private readonly IReadOnlyList<PolicySourceRegistration> _registrations;
    private readonly ConcurrentDictionary<string, PolicyAggregator> _aggregators = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PolicyResolutionResult> _resolutions = new(StringComparer.Ordinal);
    private readonly List<Task> _watcherTasks = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

    private volatile bool _initialized;

    private event EventHandler<PolicyResolutionResult>? PolicyChanged;

    public PolicyResolver(IEnumerable<PolicySourceRegistration> registrations)
    {
        if (registrations == null)
        {
            throw new ArgumentNullException(nameof(registrations));
        }

        var ordered = registrations.OrderBy(static r => r.Priority).ToList();
        if (ordered.Count == 0)
        {
            throw new ArgumentException("At least one policy source registration is required.", nameof(registrations));
        }

        _registrations = ordered;
    }

    public async ValueTask<PolicyResolutionResult> ResolveAsync(string methodId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (_resolutions.TryGetValue(methodId, out var result))
        {
            return result;
        }

        return new PolicyResolutionResult(methodId, CachePolicy.Empty, Array.Empty<PolicyContribution>(), DateTimeOffset.UtcNow);
    }

    public async IAsyncEnumerable<PolicyResolutionResult> WatchAsync(string? methodId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<PolicyResolutionResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        void Handler(object? sender, PolicyResolutionResult result)
        {
            if (methodId == null || string.Equals(result.MethodId, methodId, StringComparison.Ordinal))
            {
                channel.Writer.TryWrite(result);
            }
        }

        PolicyChanged += Handler;

        try
        {
            using var registration = cancellationToken.Register(static state => ((ChannelWriter<PolicyResolutionResult>)state!).TryComplete(new OperationCanceledException()), channel.Writer);

            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            PolicyChanged -= Handler;
            channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watcherTasks.Count == 0)
        {
            _cts.Dispose();
            _initializationGate.Dispose();
            return;
        }

        _cts.Cancel();
        try
        {
            await Task.WhenAll(_watcherTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal
        }
        finally
        {
            _cts.Dispose();
            _initializationGate.Dispose();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            foreach (var registration in _registrations)
            {
                var snapshots = await registration.Source.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

                foreach (var snapshot in snapshots)
                {
                    if (string.IsNullOrWhiteSpace(snapshot.MethodId))
                    {
                        continue;
                    }

                    ApplySnapshot(registration, snapshot, raiseNotification: false);
                }
            }

            _initialized = true;

            foreach (var registration in _registrations)
            {
                _watcherTasks.Add(Task.Run(() => WatchSourceAsync(registration, _cts.Token), CancellationToken.None));
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task WatchSourceAsync(PolicySourceRegistration registration, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in registration.Source.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                HandleChange(registration, change);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when resolver is disposed
        }
        catch (Exception)
        {
            // TODO: add logging hook
        }
    }

    private void HandleChange(PolicySourceRegistration registration, PolicyChange change)
    {
        if (string.IsNullOrWhiteSpace(change.MethodId))
        {
            return;
        }

        switch (change.Reason)
        {
            case PolicyChangeReason.Added:
            case PolicyChangeReason.Updated:
            case PolicyChangeReason.Reloaded:
                var snapshotPolicy = change.Delta.Snapshot ?? CachePolicy.Empty;
                var fields = change.Delta.SetMask;
                if (fields == CachePolicyFields.None)
                {
                    fields = CachePolicyMapper.DetectFields(snapshotPolicy);
                }

                var layer = new PolicyLayer(registration.SourceId, registration.Priority, snapshotPolicy, fields, change.Timestamp);
                UpdateResolution(change.MethodId, layer);
                break;

            case PolicyChangeReason.Removed:
                RemoveLayer(change.MethodId, registration.Priority, change.Timestamp);
                break;
        }
    }

    private void ApplySnapshot(PolicySourceRegistration registration, PolicySnapshot snapshot, bool raiseNotification)
    {
        var fields = CachePolicyMapper.DetectFields(snapshot.Policy);
        var layer = new PolicyLayer(registration.SourceId, registration.Priority, snapshot.Policy, fields, snapshot.Timestamp);
        var result = UpdateResolution(snapshot.MethodId, layer);

        if (raiseNotification)
        {
            RaisePolicyChanged(result);
        }
    }

    private PolicyResolutionResult UpdateResolution(string methodId, PolicyLayer layer)
    {
        var aggregator = _aggregators.GetOrAdd(methodId, static id => new PolicyAggregator(id));
        var result = aggregator.SetLayer(layer);
        _resolutions[methodId] = result;
        if (_initialized)
        {
            RaisePolicyChanged(result);
        }

        return result;
    }

    private void RemoveLayer(string methodId, int priority, DateTimeOffset timestamp)
    {
        if (!_aggregators.TryGetValue(methodId, out var aggregator))
        {
            return;
        }

        var result = aggregator.RemoveLayer(priority, timestamp);
        _resolutions[methodId] = result;
        if (_initialized)
        {
            RaisePolicyChanged(result);
        }
    }

    private void RaisePolicyChanged(PolicyResolutionResult result)
    {
        PolicyChanged?.Invoke(this, result);
    }

    private sealed record PolicyLayer(string SourceId, int Priority, CachePolicy Policy, CachePolicyFields Fields, DateTimeOffset Timestamp);

    private sealed class PolicyAggregator
    {
        private readonly string _methodId;
        private readonly SortedDictionary<int, PolicyLayer> _layers = new();
        private PolicyResolutionResult _current;
        private readonly object _sync = new();

        public PolicyAggregator(string methodId)
        {
            _methodId = methodId;
            _current = new PolicyResolutionResult(methodId, CachePolicy.Empty, Array.Empty<PolicyContribution>(), DateTimeOffset.UtcNow);
        }

        public PolicyResolutionResult SetLayer(PolicyLayer layer)
        {
            lock (_sync)
            {
                _layers[layer.Priority] = layer;
                _current = BuildResult();
                return _current;
            }
        }

        public PolicyResolutionResult RemoveLayer(int priority, DateTimeOffset timestamp)
        {
            lock (_sync)
            {
                _layers.Remove(priority);
                _current = BuildResult(timestamp);
                return _current;
            }
        }

        private PolicyResolutionResult BuildResult(DateTimeOffset? timestampOverride = null)
        {
            var resolvedAt = timestampOverride ?? DateTimeOffset.UtcNow;

            if (_layers.Count == 0)
            {
                return new PolicyResolutionResult(_methodId, CachePolicy.Empty, Array.Empty<PolicyContribution>(), resolvedAt);
            }

            TimeSpan? duration = null;
            IReadOnlyList<string>? tags = null;
            Type? keyGeneratorType = null;
            int? version = null;
            bool? requireIdempotent = null;
            IReadOnlyDictionary<string, string?> metadata = CachePolicy.Empty.Metadata;

            foreach (var layer in _layers.Values.OrderByDescending(static l => l.Priority))
            {
                var fields = layer.Fields;
                var policy = layer.Policy;

                if (duration == null && fields.HasFlag(CachePolicyFields.Duration))
                {
                    duration = policy.Duration;
                }

                if (tags == null && fields.HasFlag(CachePolicyFields.Tags))
                {
                    tags = policy.Tags;
                }

                if (keyGeneratorType == null && fields.HasFlag(CachePolicyFields.KeyGenerator))
                {
                    keyGeneratorType = policy.KeyGeneratorType;
                }

                if (version == null && fields.HasFlag(CachePolicyFields.Version))
                {
                    version = policy.Version;
                }

                if (requireIdempotent == null && fields.HasFlag(CachePolicyFields.RequireIdempotent))
                {
                    requireIdempotent = policy.RequireIdempotent;
                }

                if (fields.HasFlag(CachePolicyFields.Metadata) && (metadata == CachePolicy.Empty.Metadata || metadata.Count == 0))
                {
                    metadata = policy.Metadata;
                }
            }

            var contributions = _layers.Values
                .OrderBy(static l => l.Priority)
                .SelectMany(static l => l.Policy.Provenance)
                .ToList();

            var provenance = contributions.Count == 0 ? PolicyProvenance.Empty : new PolicyProvenance(contributions);

            var effectivePolicy = CachePolicy.Empty with
            {
                Duration = duration,
                Tags = tags ?? Array.Empty<string>(),
                KeyGeneratorType = keyGeneratorType,
                Version = version,
                RequireIdempotent = requireIdempotent,
                Metadata = metadata,
                Provenance = provenance
            };

            return new PolicyResolutionResult(_methodId, effectivePolicy, contributions, resolvedAt);
        }
    }
}
