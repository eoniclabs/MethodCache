using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MethodCache.Core.PolicyPipeline.Resolution;

internal sealed class PolicyResolver : IPolicyResolver, IAsyncDisposable, IDisposable
{
    private const long EmptyPolicyVersion = 0;

    private readonly IReadOnlyList<PolicySourceRegistration> _registrations;
    private readonly Dictionary<PolicySourceRegistration, int> _order = new(ReferenceEqualityComparer.Instance);
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PolicyAggregator> _aggregators = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PolicyResolutionResult> _resolutions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _warnedUnknown = new(StringComparer.Ordinal);
    private readonly bool _knowsCachedMethods;
    private readonly List<Task> _watcherTasks = new();
    private readonly List<(IImmediatePolicySource Source, Action<PolicyChange> Handler)> _immediateSubscriptions = new();
    private readonly Dictionary<PolicySourceRegistration, long> _snapshotRevisions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(LayerKey Source, string MethodId), long> _changeRevisions = new();
    private readonly object _immediateSync = new(); // Orders an immediate source's snapshot against its changes
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private int _disposed;

    private volatile bool _initialized;

    private event EventHandler<PolicyResolutionResult>? PolicyChanged;

    /// <summary>
    /// Raised synchronously, on the thread that applied the change, after a method's resolved policy changes.
    /// Results for one method can arrive out of order when sources change concurrently; compare
    /// <see cref="PolicyResolutionResult.Version"/> to keep the newest.
    /// </summary>
    internal event Action<PolicyResolutionResult>? Changed;

    public PolicyResolver(IEnumerable<PolicySourceRegistration> registrations, ILogger<PolicyResolver>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
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

        // The generated attribute source lists every cached method, so a policy for any other method can't apply
        _knowsCachedMethods = ordered.Any(IsAttributeSource);

        // Sources with the same priority, such as two AddMethodCache(configure) calls, each keep their own layer;
        // the one registered later wins where both set a value
        for (var i = 0; i < ordered.Count; i++)
        {
            _order[ordered[i]] = i;
        }
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

        return new PolicyResolutionResult(methodId, CachePolicy.Empty, Array.Empty<PolicyContribution>(), DateTimeOffset.UtcNow, version: EmptyPolicyVersion);
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

    /// <summary>
    /// Returns every resolved policy, loading the sources first if needed.
    /// </summary>
    internal async ValueTask<IReadOnlyCollection<PolicyResolutionResult>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return _resolutions.Values.ToList();
    }

    /// <summary>
    /// Stops the source watchers without waiting for them, so disposing the container synchronously works too.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Unsubscribe();

        // The watchers still observe the token, so the source is cancelled but not disposed
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        // The instance is registered under both its concrete type and its interface,
        // so the container disposes it once per registration
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Unsubscribe();

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
                if (registration.Source is IImmediatePolicySource immediate)
                {
                    SubscribeImmediate(registration, immediate);
                    continue;
                }

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

            foreach (var methodId in _aggregators.Keys)
            {
                WarnIfUnknown(methodId);
            }

            foreach (var registration in _registrations.Where(static r => r.Source is not IImmediatePolicySource))
            {
                _watcherTasks.Add(Task.Run(() => WatchSourceAsync(registration, _cts.Token), CancellationToken.None));
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <summary>
    /// Applies an immediate source's changes on the thread that makes them. Subscribing and reading the snapshot under
    /// one lock means no change is lost or applied before the snapshot it follows.
    /// </summary>
    private void SubscribeImmediate(PolicySourceRegistration registration, IImmediatePolicySource source)
    {
        void Handler(PolicyChange change)
        {
            lock (_immediateSync)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    HandleChange(registration, change);
                }
            }
        }

        lock (_immediateSync)
        {
            source.Changed += Handler;
            _immediateSubscriptions.Add((source, Handler));

            IReadOnlyCollection<PolicySnapshot> snapshots;
            if (source is RuntimePolicySource runtime)
            {
                var captured = runtime.CaptureSnapshots();
                snapshots = captured.Snapshots;
                _snapshotRevisions[registration] = captured.Revision;
            }
            else
            {
                snapshots = source.GetSnapshot();
            }
            foreach (var snapshot in snapshots)
            {
                if (!string.IsNullOrWhiteSpace(snapshot.MethodId))
                {
                    ApplySnapshot(registration, snapshot, raiseNotification: false);
                }
            }
        }
    }

    private void Unsubscribe()
    {
        lock (_immediateSync)
        {
            foreach (var (source, handler) in _immediateSubscriptions)
            {
                source.Changed -= handler;
            }

            _immediateSubscriptions.Clear();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "MethodCache stopped watching the policy source '{SourceId}'; its policies stay as they were last read.", registration.SourceId);
        }
    }

    private void HandleChange(PolicySourceRegistration registration, PolicyChange change)
    {
        if (string.IsNullOrWhiteSpace(change.MethodId))
        {
            return;
        }

        if (change.SourceRevision > 0)
        {
            var key = (KeyOf(registration), change.MethodId);
            var seen = Math.Max(_snapshotRevisions.GetValueOrDefault(registration), _changeRevisions.GetValueOrDefault(key));
            if (change.SourceRevision <= seen)
            {
                return;
            }
            _changeRevisions[key] = change.SourceRevision;
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

                UpdateResolution(change.MethodId, new PolicyLayer(KeyOf(registration), snapshotPolicy, fields, change.Timestamp));
                break;

            case PolicyChangeReason.Removed:
                RemoveLayer(change.MethodId, KeyOf(registration), change.Timestamp);
                break;
        }
    }

    private void ApplySnapshot(PolicySourceRegistration registration, PolicySnapshot snapshot, bool raiseNotification)
    {
        // The fields the source declared, as a change carries them, so a snapshot and a change resolve alike:
        // cleared tags stay cleared, and an unset value doesn't hide a default
        var declared = snapshot.Policy.Provenance.LastOrDefault(c => c.SourceId == snapshot.SourceId)?.Fields ?? CachePolicyFields.None;
        var fields = declared != CachePolicyFields.None ? declared : CachePolicyMapper.DetectFields(snapshot.Policy);
        UpdateResolution(snapshot.MethodId, new PolicyLayer(KeyOf(registration), snapshot.Policy, fields, snapshot.Timestamp));
    }

    private LayerKey KeyOf(PolicySourceRegistration registration) => new(registration.Priority, _order[registration]);

    private static bool IsAttributeSource(PolicySourceRegistration registration)
        => registration.Priority == PolicySourcePriority.Attributes &&
           string.Equals(registration.SourceId, PolicySourceIds.Attributes, StringComparison.Ordinal);

    private void UpdateResolution(string methodId, PolicyLayer layer)
    {
        var aggregator = _aggregators.GetOrAdd(methodId, static id => new PolicyAggregator(id));
        aggregator.SetLayer(layer);
        Refresh(methodId);
        if (_initialized)
        {
            WarnIfUnknown(methodId);
        }
    }

    /// <summary>
    /// Warns once about a policy for a method that no <c>[Cache]</c> attribute marks, usually a typo in a method
    /// name in appsettings or code, since such a policy never applies.
    /// </summary>
    private void WarnIfUnknown(string methodId)
    {
        if (!_knowsCachedMethods ||
            MethodIds.IsDefaults(methodId) ||
            !_aggregators.TryGetValue(methodId, out var aggregator) ||
            aggregator.Layers.Any(static layer => layer.Key.Priority == PolicySourcePriority.Attributes) ||
            !_warnedUnknown.TryAdd(methodId, true))
        {
            return;
        }

        _logger.LogWarning(
            "MethodCache has a policy for '{MethodId}', but no cached method has that id, so the policy does nothing. " +
            "Method ids have the form Namespace.IService.Method; check the spelling and that the method has [Cache].",
            methodId);
    }

    private void RemoveLayer(string methodId, LayerKey key, DateTimeOffset timestamp)
    {
        if (!_aggregators.TryGetValue(methodId, out var aggregator))
        {
            return;
        }

        aggregator.RemoveLayer(key);
        Refresh(methodId, timestamp);
    }

    /// <summary>
    /// Re-resolves a method after one of its layers changed. Defaults apply to many methods, so a change to the
    /// global or a group's defaults re-resolves every method.
    /// </summary>
    private void Refresh(string changedId, DateTimeOffset? timestamp = null)
    {
        if (!MethodIds.IsDefaults(changedId))
        {
            Publish(_aggregators[changedId], timestamp);
            return;
        }

        foreach (var aggregator in _aggregators.Values)
        {
            if (!MethodIds.IsDefaults(aggregator.MethodId))
            {
                Publish(aggregator, timestamp);
            }
        }
    }

    private void Publish(PolicyAggregator aggregator, DateTimeOffset? timestamp)
    {
        var result = aggregator.Rebuild(DefaultLayers, timestamp);

        // Rebuilt under the aggregator's lock but stored after it, so a slower publisher must not replace a newer result
        _resolutions.AddOrUpdate(
            aggregator.MethodId,
            static (_, added) => added,
            static (_, existing, added) => existing.Version > added.Version ? existing : added,
            result);
        if (_initialized)
        {
            RaisePolicyChanged(result);
        }
    }

    private PolicyLayer[] DefaultLayers(string defaultsId)
        => _aggregators.TryGetValue(defaultsId, out var aggregator) ? aggregator.Layers : Array.Empty<PolicyLayer>();

    private void RaisePolicyChanged(PolicyResolutionResult result)
    {
        Changed?.Invoke(result);
        PolicyChanged?.Invoke(this, result);
    }

    private sealed record PolicyLayer(LayerKey Key, CachePolicy Policy, CachePolicyFields Fields, DateTimeOffset Timestamp);

    /// <summary>
    /// Identifies a layer by priority and registration order, so every registered source keeps its own layer and,
    /// at equal priority, the one registered later takes precedence.
    /// </summary>
    private readonly record struct LayerKey(int Priority, int Order) : IComparable<LayerKey>
    {
        public int CompareTo(LayerKey other)
        {
            var byPriority = Priority.CompareTo(other.Priority);
            return byPriority != 0 ? byPriority : Order.CompareTo(other.Order);
        }
    }

    /// <summary>
    /// Holds one method's layers, or the layers of the defaults for all methods or for one group.
    /// </summary>
    private sealed class PolicyAggregator
    {
        private readonly SortedDictionary<LayerKey, PolicyLayer> _layers = new();
        private readonly object _sync = new();
        private long _version; // Increments on each rebuild

        public PolicyAggregator(string methodId)
        {
            MethodId = methodId;
        }

        public string MethodId { get; }

        /// <summary>The layers from lowest to highest priority.</summary>
        public PolicyLayer[] Layers
        {
            get
            {
                lock (_sync)
                {
                    return _layers.Values.ToArray();
                }
            }
        }

        public void SetLayer(PolicyLayer layer)
        {
            lock (_sync)
            {
                _layers[layer.Key] = layer;
            }
        }

        public void RemoveLayer(LayerKey key)
        {
            lock (_sync)
            {
                _layers.Remove(key);
            }
        }

        /// <summary>
        /// Resolves the method's policy: each setting comes from the method's own highest-priority layer that sets it,
        /// then from its group's defaults, then from the defaults for all methods. So a value in <c>[Cache]</c> wins
        /// over a default, whichever source the default comes from.
        /// </summary>
        public PolicyResolutionResult Rebuild(Func<string, PolicyLayer[]> defaultLayers, DateTimeOffset? timestampOverride)
        {
            lock (_sync)
            {
                var resolvedAt = timestampOverride ?? DateTimeOffset.UtcNow;
                var currentVersion = ++_version;

                if (_layers.Count == 0)
                {
                    return new PolicyResolutionResult(MethodId, CachePolicy.Empty, Array.Empty<PolicyContribution>(), resolvedAt, currentVersion);
                }

                // Metadata merges key by key, higher layers winning, so a runtime override's audit notes don't drop
                // the method's group
                IReadOnlyDictionary<string, string?> metadata = CachePolicy.Empty.Metadata;
                Dictionary<string, string?>? merged = null;
                foreach (var layer in _layers.Values)
                {
                    if (layer.Fields.HasFlag(CachePolicyFields.Metadata) && layer.Policy.Metadata.Count > 0)
                    {
                        merged ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                        foreach (var (key, value) in layer.Policy.Metadata)
                        {
                            merged[key] = value;
                        }
                    }
                }

                if (merged != null)
                {
                    metadata = merged;
                }

                // Highest precedence first: the method's own layers, then its group's defaults, then the global ones
                var layers = new List<PolicyLayer>(_layers.Values.Reverse());
                if (metadata.TryGetValue("group", out var group) && !string.IsNullOrWhiteSpace(group))
                {
                    layers.AddRange(defaultLayers(MethodIds.Group(group)).Reverse());
                }

                layers.AddRange(defaultLayers(MethodIds.AllMethods).Reverse());

                TimeSpan? duration = null;
                List<string>? tags = null;
                var tagsCleared = false;
                TimeSpan? slidingExpiration = null;
                TimeSpan? refreshAhead = null;
                Func<object?, bool>? cacheWhen = null;
                var cacheWhenSet = false;
                TimeSpan? failSafe = null;
                TimeSpan? failSafeTimeout = null;
                TimeSpan? failSafeRetryAfter = null;
                int? version = null;
                List<PolicyContribution>? contributions = null;

                foreach (var layer in layers)
                {
                    var fields = layer.Fields;
                    var policy = layer.Policy;

                    if (duration == null && fields.HasFlag(CachePolicyFields.Duration))
                    {
                        duration = policy.Duration;
                    }

                    // Tags combine across layers, so an override never disconnects the attribute's [CacheInvalidate]
                    // tags; a layer that clears them stops the lower layers from adding theirs
                    if (!tagsCleared && (fields & (CachePolicyFields.Tags | CachePolicyFields.ClearTags)) != 0)
                    {
                        tags ??= new List<string>();
                        var layerTags = policy.Tags.Distinct(StringComparer.Ordinal).Where(tag => !tags.Contains(tag, StringComparer.Ordinal)).ToList();
                        tags.InsertRange(0, layerTags);
                        tagsCleared = fields.HasFlag(CachePolicyFields.ClearTags);
                    }

                    if (slidingExpiration == null && fields.HasFlag(CachePolicyFields.SlidingExpiration))
                    {
                        slidingExpiration = policy.SlidingExpiration;
                    }

                    if (refreshAhead == null && fields.HasFlag(CachePolicyFields.RefreshAhead))
                    {
                        refreshAhead = policy.RefreshAhead;
                    }

                    if (!cacheWhenSet && fields.HasFlag(CachePolicyFields.CacheWhen))
                    {
                        cacheWhen = policy.CacheWhen;
                        cacheWhenSet = true;
                    }

                    if (failSafe == null && fields.HasFlag(CachePolicyFields.FailSafe))
                    {
                        failSafe = policy.FailSafe;
                    }

                    if (failSafeTimeout == null && fields.HasFlag(CachePolicyFields.FailSafeTimeout))
                    {
                        failSafeTimeout = policy.FailSafeTimeout;
                    }

                    if (failSafeRetryAfter == null && fields.HasFlag(CachePolicyFields.FailSafeRetryAfter))
                    {
                        failSafeRetryAfter = policy.FailSafeRetryAfter;
                    }

                    if (version == null && fields.HasFlag(CachePolicyFields.Version))
                    {
                        version = policy.Version;
                    }

                    if (policy.Provenance.Count > 0)
                    {
                        contributions ??= new List<PolicyContribution>();
                        contributions.InsertRange(0, policy.Provenance);
                    }
                }

                var effectivePolicy = CachePolicy.Empty with
                {
                    Duration = duration,
                    Tags = tags is { Count: > 0 } ? tags.ToArray() : Array.Empty<string>(),
                    SlidingExpiration = slidingExpiration,
                    RefreshAhead = refreshAhead,
                    CacheWhen = cacheWhen,
                    FailSafe = failSafe,
                    FailSafeTimeout = failSafeTimeout,
                    FailSafeRetryAfter = failSafeRetryAfter,
                    Version = version,
                    Metadata = metadata,
                    Provenance = contributions is { Count: > 0 } ? new PolicyProvenance(contributions) : PolicyProvenance.Empty
                };

                return new PolicyResolutionResult(
                    MethodId,
                    effectivePolicy,
                    contributions is { Count: > 0 } ? contributions : Array.Empty<PolicyContribution>(),
                    resolvedAt,
                    currentVersion);
            }
        }
    }
}
