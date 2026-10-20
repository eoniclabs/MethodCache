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
    private readonly IReadOnlyDictionary<string, string[]>? _parameterNames;
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

    /// <param name="registrations">The policy sources.</param>
    /// <param name="logger">Logs a source that stops being watched.</param>
    /// <param name="parameterNames">
    /// The parameter names of each cached method, which tag templates from code, configuration and runtime overrides
    /// are checked against; without them templates aren't checked.
    /// </param>
    public PolicyResolver(IEnumerable<PolicySourceRegistration> registrations, ILogger<PolicyResolver>? logger = null,
        IReadOnlyDictionary<string, string[]>? parameterNames = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _parameterNames = parameterNames;
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

            try
            {
                // In priority order, so the attribute and startup-code layers are in place when a reloading source
                // checks its policies against them
                foreach (var registration in _registrations)
                {
                    if (registration.Source is IImmediatePolicySource immediate)
                    {
                        if (immediate is IUsesPolicyLoadChecks checkedSource)
                        {
                            checkedSource.UseChecks(ChecksFor(KeyOf(registration)));
                        }

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

                CheckLoadedPolicies();
            }
            catch
            {
                // A later call loads again from the start
                Unsubscribe();
                throw;
            }

            _initialized = true;

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

            var captured = source.CaptureSnapshots();
            var snapshots = captured.Snapshots;
            _snapshotRevisions[registration] = captured.Revision;
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
            _logger.LogError("MethodCache stopped watching the policy source '{SourceId}'; its policies stay as they were last read. Error type: {ErrorType}.", registration.SourceId, ex.GetType().FullName);
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
    }

    private static bool IsAttributeLayer(PolicyLayer layer) => layer.Key.Priority == PolicySourcePriority.Attributes;

    /// <summary>The ids of the methods with <c>[Cache]</c>, which the generated attribute sources list.</summary>
    private HashSet<string> CachedMethodIds()
        => _aggregators.Where(static pair => !MethodIds.IsDefaults(pair.Key) && pair.Value.Layers.Any(IsAttributeLayer))
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

    private PolicyLoadChecks ChecksFor(LayerKey key)
        => new(
            () => _knowsCachedMethods ? CachedMethodIds() : Array.Empty<string>(),
            drafts => FindTagProblems(key, drafts.Select(draft => (draft.MethodId, draft.Policy, draft.Fields)), replaceAll: true).FirstOrDefault());

    /// <summary>
    /// The first tag template problem a runtime override for <paramref name="methodId"/> would cause, or
    /// <see langword="null"/>.
    /// </summary>
    internal TagProblem? FindOverrideTagProblem(string methodId, CachePolicy policy, CachePolicyFields fields)
    {
        var registration = _registrations.FirstOrDefault(static r => r.Source is RuntimePolicySource);
        return registration == null
            ? null
            : FindTagProblems(KeyOf(registration), [(methodId, policy, fields)], replaceAll: false).FirstOrDefault();
    }

    /// <summary>
    /// Fails loading for a policy whose method isn't cached, usually a misspelled id or a method without
    /// <c>[Cache]</c>, and for a tag template that doesn't fit a cached method it reaches. Configuration checks its own
    /// policies as it reads them, so its errors name the configuration path; <c>[Cache]</c> is checked by the generator.
    /// </summary>
    private void CheckLoadedPolicies()
    {
        if (_knowsCachedMethods)
        {
            var cached = CachedMethodIds();
            foreach (var (methodId, aggregator) in _aggregators.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                var layers = aggregator.Layers;
                if (MethodIds.IsDefaults(methodId) || layers.Length == 0 || cached.Contains(methodId))
                {
                    continue;
                }

                var configures = layers.Any(static layer => layer.Key.Priority == PolicySourcePriority.StartupFluent)
                    ? "mc.Method(...) configures a method that isn't cached. "
                    : $"The policy source '{layers[^1].Policy.Provenance.LastOrDefault()?.SourceId}' configures a method that isn't cached. ";
                throw new InvalidOperationException(configures + MethodIds.NotCached(methodId, cached));
            }
        }

        if (FindTagProblems(replaced: null, [], replaceAll: false).FirstOrDefault() is { } problem)
        {
            throw new InvalidOperationException(
                $"The tag '{problem.Tag}' set in {problem.Where} reaches the cached method '{problem.MethodId}', but {problem.Reason}.");
        }
    }

    /// <summary>
    /// Checks every tag template that reaches a cached method from code, configuration or a runtime override against
    /// the method's parameter names: its own layers, then its group's defaults, then the defaults for all methods, as
    /// the policy resolves. With <paramref name="replaced"/>, that source's layers are replaced by
    /// <paramref name="candidate"/> first: all of them with <paramref name="replaceAll"/>, otherwise only those of
    /// the candidate's ids.
    /// </summary>
    private IEnumerable<TagProblem> FindTagProblems(LayerKey? replaced,
        IEnumerable<(string MethodId, CachePolicy Policy, CachePolicyFields Fields)> candidate, bool replaceAll)
    {
        if (_parameterNames == null)
        {
            yield break;
        }

        var replacements = candidate.ToDictionary(
            static item => item.MethodId,
            item => new PolicyLayer(replaced!.Value, item.Policy, item.Fields, DateTimeOffset.MinValue),
            StringComparer.Ordinal);

        PolicyLayer[] LayersOf(string id)
        {
            var layers = _aggregators.TryGetValue(id, out var aggregator) ? aggregator.Layers : Array.Empty<PolicyLayer>();
            if (replaced is not { } key || (!replaceAll && !replacements.ContainsKey(id)))
            {
                return layers;
            }

            var kept = layers.Where(layer => layer.Key != key).ToList();
            if (replacements.TryGetValue(id, out var replacement))
            {
                kept.Add(replacement);
                kept.Sort(static (left, right) => left.Key.CompareTo(right.Key));
            }

            return kept.ToArray();
        }

        foreach (var methodId in _aggregators.Keys.Concat(replacements.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (MethodIds.IsDefaults(methodId) || !_parameterNames.TryGetValue(methodId, out var parameterNames))
            {
                continue;
            }

            var own = LayersOf(methodId);
            if (!own.Any(IsAttributeLayer))
            {
                continue;
            }

            // The group comes from the highest layer that sets it, as metadata merges
            string? group = null;
            foreach (var layer in own)
            {
                if (layer.Fields.HasFlag(CachePolicyFields.Metadata) && layer.Policy.Metadata.TryGetValue("group", out var value))
                {
                    group = value;
                }
            }

            var reaching = own.Reverse().Select(layer => (Id: methodId, Layer: layer));
            if (!string.IsNullOrWhiteSpace(group))
            {
                reaching = reaching.Concat(LayersOf(MethodIds.Group(group)).Reverse().Select(layer => (Id: MethodIds.Group(group), Layer: layer)));
            }

            reaching = reaching.Concat(LayersOf(MethodIds.AllMethods).Reverse().Select(static layer => (Id: MethodIds.AllMethods, Layer: layer)));
            foreach (var (layerId, layer) in reaching)
            {
                if ((layer.Fields & (CachePolicyFields.Tags | CachePolicyFields.ClearTags)) == 0)
                {
                    continue;
                }

                if (!IsAttributeLayer(layer))
                {
                    foreach (var tag in layer.Policy.Tags)
                    {
                        if (TagTemplateRules.Check(tag, parameterNames) is { } reason)
                        {
                            yield return new TagProblem(methodId, layerId, tag, reason, Describe(layer, layerId), layer.Key == replaced);
                        }
                    }
                }

                // A layer that clears tags keeps the lower layers' tags off the method
                if (layer.Fields.HasFlag(CachePolicyFields.ClearTags))
                {
                    break;
                }
            }
        }
    }

    private static string Describe(PolicyLayer layer, string layerId)
    {
        var group = MethodIds.GroupName(layerId);
        return layer.Key.Priority switch
        {
            PolicySourcePriority.StartupFluent when layerId == MethodIds.AllMethods => "mc.Defaults(...)",
            PolicySourcePriority.StartupFluent when group != null => $"mc.Group(\"{group}\", ...)",
            PolicySourcePriority.StartupFluent => "mc.Method(...)",
            PolicySourcePriority.ConfigurationFiles when layerId == MethodIds.AllMethods => "the configuration's Defaults",
            PolicySourcePriority.ConfigurationFiles when group != null => $"the configuration's Groups:{group}",
            PolicySourcePriority.ConfigurationFiles => "the configuration's Methods",
            PolicySourcePriority.RuntimeOverrides => "a runtime override",
            _ => $"the policy source '{layer.Policy.Provenance.LastOrDefault()?.SourceId}'"
        };
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

                var (effectivePolicy, fieldSources) = ResolveLayers(layers, metadata);
                var (declaration, _) = ResolveLayers(layers.Where(static layer => !layer.Policy.Provenance.Any(static contribution => contribution.SourceId == "runtime-overrides")), metadata);

                return new PolicyResolutionResult(
                    MethodId,
                    effectivePolicy,
                    effectivePolicy.Provenance.ToArray(),
                    resolvedAt,
                    currentVersion)
                { Declaration = declaration, FieldSources = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CacheSettingSource>(fieldSources) };
            }
        }

        // The settings SettingSources reports, by the CacheEntryOptions property they set; a layer that clears tags sets Tags
        private static readonly (CachePolicyFields Fields, string Setting)[] ReportedSettings =
        [
            (CachePolicyFields.Duration, nameof(CacheEntryOptions.Duration)),
            (CachePolicyFields.Tags | CachePolicyFields.ClearTags, nameof(CacheEntryOptions.Tags)),
            (CachePolicyFields.SlidingExpiration, nameof(CacheEntryOptions.SlidingExpiration)),
            (CachePolicyFields.RefreshAhead, nameof(CacheEntryOptions.RefreshAhead)),
            (CachePolicyFields.Version, nameof(CacheEntryOptions.Version)),
            (CachePolicyFields.FailSafe, nameof(CacheEntryOptions.FailSafe)),
            (CachePolicyFields.FailSafeTimeout, nameof(CacheEntryOptions.FailSafeTimeout)),
            (CachePolicyFields.FailSafeRetryAfter, nameof(CacheEntryOptions.FailSafeRetryAfter)),
            (CachePolicyFields.Enabled, nameof(CacheEntryOptions.Enabled)),
            (CachePolicyFields.Distributed, nameof(CacheEntryOptions.Distributed)),
        ];

        private static CacheSettingSource? SettingSourceOf(string? sourceId) => sourceId switch
        {
            PolicySourceIds.Attributes => CacheSettingSource.Attribute,
            PolicySourceIds.StartupFluent => CacheSettingSource.Code,
            PolicySourceIds.ConfigurationFiles => CacheSettingSource.Configuration,
            PolicySourceIds.RuntimeOverrides => CacheSettingSource.RuntimeOverride,
            _ => null
        };

        private static (CachePolicy Policy, Dictionary<string, CacheSettingSource> Sources) ResolveLayers(IEnumerable<PolicyLayer> layers, IReadOnlyDictionary<string, string?> metadata)
        {
            var canonicalTagParts = true;
            bool? enabled = null;
            bool? distributed = null;
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
            var fieldSources = new Dictionary<string, CacheSettingSource>(StringComparer.Ordinal);

            foreach (var layer in layers)
            {
                var fields = layer.Fields;
                var policy = layer.Policy;
                var sourceId = policy.Provenance.LastOrDefault()?.SourceId;
                if (sourceId == PolicySourceIds.Attributes) canonicalTagParts = policy.CanonicalTagParts;
                // Layers come highest first, so the first that sets a setting is the one it comes from
                if (SettingSourceOf(sourceId) is { } settingSource)
                {
                    foreach (var (settingFields, setting) in ReportedSettings)
                    {
                        if ((fields & settingFields) != 0)
                        {
                            fieldSources.TryAdd(setting, settingSource);
                        }
                    }
                }

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

                if (enabled == null && fields.HasFlag(CachePolicyFields.Enabled)) enabled = policy.Enabled;
                if (distributed == null && fields.HasFlag(CachePolicyFields.Distributed)) distributed = policy.Distributed;

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
                Enabled = enabled,
                CanonicalTagParts = canonicalTagParts,
                Distributed = distributed,
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

            return (effectivePolicy, fieldSources);
        }
    }
}
