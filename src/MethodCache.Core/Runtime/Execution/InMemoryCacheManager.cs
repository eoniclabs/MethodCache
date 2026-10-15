using System.Collections.Concurrent;
using System.Diagnostics;
using MethodCache.Core.Telemetry;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Options;

namespace MethodCache.Extensibility
{
    /// <summary>
    /// High-performance in-memory cache manager.
    /// </summary>
    /// <remarks>
    /// <para>Storage and eviction are handled by a W-TinyLFU engine (the algorithm behind Caffeine): reads are
    /// lock-free, eviction and expiration are O(1), and a frequency sketch keeps popular entries resident.</para>
    /// <para>On top of that the manager provides: single-flight loading (one factory execution per key, however
    /// many concurrent callers), eager background refresh before expiry, probabilistic early refresh (XFetch),
    /// sliding expiration, fail-safe fallback to stale values when a factory throws, TTL jitter, optional
    /// caching of null results, and tag / wildcard invalidation that also cancels in-flight loads from being stored.</para>
    /// <para>With a distributed cache configured (<c>AddMethodCacheDistributedCache</c>), an in-memory miss reads the
    /// distributed cache before running the factory, loaded values are written to both, and key, tag and clear
    /// invalidations reach the distributed cache and, through a backplane, other instances' memory.</para>
    /// </remarks>
    internal partial class InMemoryCacheManager : ICacheRuntime, IMemoryCache
    {
        private const long NanosPerTick = 100;

        /// <summary>Upper bound for any duration (~73 years); leaves headroom so sums of durations cannot overflow.</summary>
        private const long MaxNanos = long.MaxValue / 4;

        private static readonly ConditionalWeakTable<CacheRuntimePolicy, EntryPolicy> PolicyCache = new();
        private static readonly AsyncLocal<BulkPublication?> CurrentBulkPublication = new();

        [ThreadStatic]
        private static EntryPolicy? t_lastEntryPolicy;

        private readonly MemoryCacheEngine _engine;
        private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<BulkLoad, byte> _bulkLoads = new();
        private readonly CoreTelemetry? _telemetry;
        private readonly MethodCacheMemoryOptions _options;
        private readonly Func<string, object?, long>? _weigher;
        private readonly ITimer? _maintenanceTimer;
        private readonly StripedCounter _hits = new();
        private readonly StripedCounter _misses = new();

        private readonly long _defaultDuration;
        private readonly long _maxDuration;
        private readonly TimeProvider _timeProvider;
        private readonly long _jitter;
        private readonly DistributedCacheLayer? _distributed;
        private readonly Action<string>? _onMethodInvalidated;
        private readonly GeneratedDiagnostics _syncDiagnostics;

        /// <summary>Whether removals also go to a distributed cache and, through it, to the other instances.</summary>
        internal bool HasDistributedCache => _distributed != null;

        /// <summary>Whether the distributed cache is in use now; null without one.</summary>
        internal async ValueTask<bool?> CheckDistributedCacheAsync(CancellationToken cancellationToken) =>
            _distributed == null ? null : await _distributed.CheckAvailableAsync(cancellationToken).ConfigureAwait(false);

        private int _disposed;

        /// <summary>Creates a cache manager with container-owned instrumentation and optional distributed storage.</summary>
        internal InMemoryCacheManager(IOptions<MethodCacheMemoryOptions>? options = null, TimeProvider? timeProvider = null,
            DistributedCacheLayer? distributed = null, Action<string>? onMethodInvalidated = null,
            Microsoft.Extensions.Logging.ILogger? logger = null, CoreTelemetry? telemetry = null)
        {
            _telemetry = telemetry;
            _options = options?.Value ?? new MethodCacheMemoryOptions();
            _weigher = _options.Weigher;

            _defaultDuration = ToNanos(_options.DefaultExpiration);
            _maxDuration = ToNanos(_options.MaxExpiration);
            _timeProvider = timeProvider ?? TimeProvider.System;
            _jitter = ToNanos(_options.JitterMaxDuration);

            var maximum = _weigher != null ? _options.MaxMemoryBytes : _options.MaxItems;
            _engine = new MemoryCacheEngine(maximum, ToEngineMode(_options.EvictionPolicy), CreateClock(timeProvider), (int)Math.Min(_options.MaxItems, int.MaxValue));

            if (_options.EnableBackgroundCleanup)
            {
                _maintenanceTimer = MaintenanceTimer.Start(_timeProvider, _engine, _options.CleanupInterval);
            }

            _distributed = distributed;
            _telemetry?.ObserveMethods(() => new CacheObservation(_engine.Count, 0, 0, 0, _engine.TotalWeight));
            _onMethodInvalidated = onMethodInvalidated;
            _syncDiagnostics = new GeneratedDiagnostics(logger);
            _distributed?.Attach(
                onKeyInvalidated: key => RemoveLocal(key),
                onTagInvalidated: RemoveLocalTag,
                onCleared: ClearLocal,
                onError: static _ => { },
                onMethodInvalidated: methodId => { _onMethodInvalidated?.Invoke(methodId); InvalidateMethodLocally(methodId); });
        }

        /// <summary>The underlying engine; exposed to tests that need deterministic policy behavior.</summary>
        internal MemoryCacheEngine Engine => _engine;
        internal CoreTelemetry? Telemetry => _telemetry;
        private readonly ConcurrentDictionary<string, SyncFlight> _syncFlights = new(StringComparer.Ordinal);

        internal T GetOrCreateSynchronously<T>(string key, string name, Func<T> factory, CacheRuntimePolicy source, CancellationToken token)
        {
            name = source.MethodId;
            var outcome = RequestResult.Error;
            try
            {
                var policy = GetEntryPolicy(source);
                if (source.Enabled == false || source.Generation?.IsCurrent == false)
                {
                    outcome = RequestResult.Bypass;
                    using var bypass = _telemetry?.BeginLoad("method", name, key, source.Tags);
                    try { return factory(); } catch (Exception error) { bypass?.Fail(error); throw; }
                }
                if (TryGetFresh<T>(key, _engine.Now, out var hit, out var node, out var refresh))
                {
                    if (refresh) StartBackgroundRefresh(key, name, () => Task.FromResult(factory()), policy, node!, token);
                    RecordHit(name);
                    outcome = HitResult(node);
                    return hit!;
                }
                token.ThrowIfCancellationRequested();
                if (_distributed != null && source.Distributed != false)
                    _syncDiagnostics.ReportSyncBypass(name, "The provider offers no bounded native synchronous operation; only memory is used.");
                var own = new SyncFlight(policy.Tags, source.Generation) { MethodId = source.MethodId };
                SyncFlight flight;
                while (true)
                {
                    flight = _syncFlights.GetOrAdd(key, own);
                    if (ReferenceEquals(flight, own)) break;
                    if (flight.Invalidated)
                    {
                        if (_syncFlights.TryUpdate(key, own, flight)) { flight = own; break; }
                        continue;
                    }
                    if (flight.OwnerThread == Environment.CurrentManagedThreadId || LoadingScope.Contains(flight))
                        throw new InvalidOperationException($"The factory of '{name}' recursively requested its own key.");
                    flight.Ready.Wait(token);
                    if (flight.CancelledByCaller && !token.IsCancellationRequested) continue;
                    flight.Error?.Throw();
                    if (flight.Value is T joined) { RecordHit(name); outcome = flight.Outcome == RequestResult.Stale ? RequestResult.Stale : RequestResult.Hit; return joined; }
                    if (flight.Value == null && default(T) == null) { RecordHit(name); outcome = flight.Outcome == RequestResult.Stale ? RequestResult.Stale : RequestResult.Hit; return default!; }
                    outcome = RequestResult.Miss;
                    using var independent = _telemetry?.BeginLoad("method", name, key, source.Tags);
                    try { return factory(); } catch (Exception error) { independent?.Fail(error); throw; }
                }
                Node? previous = policy.FailSafe > 0 && _engine.TryPeek(key, out var peeked) ? peeked : null;
                try
                {
                    if (TryGetFresh<T>(key, _engine.Now, out hit, out var racedNode, out _))
                    {
                        flight.Value = hit;
                        flight.Complete(hit);
                        RecordHit(name);
                        outcome = HitResult(racedNode);
                        return hit!;
                    }
                    RecordMiss(name);
                    T value;
                    using (var origin = _telemetry?.BeginLoad("method", name, key, policy.Tags))
                    {
                        try { value = LoadingScope.Run(flight, factory); }
                        catch (Exception error) { origin?.Fail(error); throw; }
                    }
                    outcome = RequestResult.Miss;
                    if (ShouldStore(value, policy, name)) StoreLoaded(key, value, policy, flight);
                    flight.Value = value;
                    flight.Complete(value);
                    return value;
                }
                catch (Exception exception)
                {
                    if (exception is OperationCanceledException cancelled && token.IsCancellationRequested)
                    {
                        flight.Error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                        flight.CancelByCaller(cancelled);
                        throw;
                    }
                    if (exception is not OperationCanceledException && previous != null && !flight.Invalidated && TryServeStale<T>(previous, policy, out var stale))
                    {
                        outcome = flight.Outcome = RequestResult.Stale;
                        flight.Value = stale;
                        flight.Complete(stale);
                        return stale!;
                    }
                    flight.Error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                    flight.Fail(exception);
                    throw;
                }
                finally
                {
                    _syncFlights.TryRemove(new KeyValuePair<string, SyncFlight>(key, flight));
                    flight.Ready.Set();
                }
            }
            catch { outcome = RequestResult.Error; throw; }
            finally { _telemetry?.Request("method", name, outcome); }
        }

        internal void InvalidateTagsSynchronously(string[] tags)
        {
            tags = tags.Distinct(StringComparer.Ordinal).ToArray();
            using var activity = _distributed == null ? _telemetry?.BeginInvalidation("tag", tags: tags) : null;
            foreach (var tag in tags) RemoveLocalTag(tag);
            _telemetry?.Invalidations("tag", "local", tags.Length);
            if (_distributed != null)
            {
                var recovery = _distributed.InvalidateTargetsAsync(tags, tags: true);
                _ = recovery.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _syncDiagnostics.ReportSyncBypass("CacheInvalidate", "Distributed invalidation is owned by the asynchronous recovery path.");
            }
        }



        /// <summary>Whether a load or background refresh for <paramref name="key"/> is still running; lets tests wait for one to finish.</summary>
        internal bool IsLoading(string key) => _flights.ContainsKey(key);

        /// <summary>Guards publication during a batch source read without reserving its keys against other writers.</summary>
        internal BulkLoad BeginBulkLoad(IEnumerable<string> keys, CacheRuntimePolicy policy)
        {
            var batch = new BulkLoad(this, keys, policy);
            _bulkLoads.TryAdd(batch, 0);
            return batch;
        }

        #region ICacheManager

        /// <inheritdoc />
        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(keyGenerator);

            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return GetOrCreateCoreAsync(key, methodName, factory, GetEntryPolicy(policy), CancellationToken.None);
        }

        /// <inheritdoc />
        public Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("Cache key is required", nameof(cacheKey));
            }

            ArgumentNullException.ThrowIfNull(policy);
            methodName = policy.MethodId;
            var bulk = CurrentBulkPublication.Value;
            var publication = bulk != null && ReferenceEquals(bulk.Owner, this) && bulk.Key == cacheKey ? bulk.Guard : null;
            return GetOrCreateCoreAsync(cacheKey, methodName, factory, GetEntryPolicy(policy), cancellationToken, publication);
        }

        /// <inheritdoc />
        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(keyGenerator);

            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return new ValueTask<T?>(TryGetFresh<T>(key, _engine.Now, out var value, out _, out _) ? value : default);
        }

        /// <inheritdoc />
        public ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(keyGenerator);
            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return new(TryGetFresh<T>(key, _engine.Now, out var value, out _, out _)
                ? CacheLookupResult<T>.Hit(value!) : CacheLookupResult<T>.Miss);
        }

        /// <inheritdoc />
        public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
        {
            return new ValueTask<T?>(TryGetFast<T>(cacheKey, out var value) ? value : default);
        }

        /// <summary>
        /// Synchronous lookup used by generated decorators. Returns false (without counting a miss) when the
        /// entry is due for an early refresh, so the caller takes the loading path that starts the refresh.
        /// </summary>
        public bool TryGetFast<T>(string cacheKey, out T? value)
        {
            if (TryGetFastNode(cacheKey, out value, out _))
            {
                return true;
            }

            value = default;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetFastTask<T>(string cacheKey, [NotNullWhen(true)] out Task<T>? task)
        {
            if (TryGetFastNode<T>(cacheKey, out var value, out var node))
            {
                // The entry keeps its completed task, so repeated hits return the same instance
                task = node is ValueNode<T> typed ? typed.AsCompletedTask() : Task.FromResult(value!);
                return true;
            }

            task = null;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetFast<T>(ReadOnlySpan<char> cacheKey, out T? value)
        {
            if (TryGetFastNode(cacheKey, out value, out _))
            {
                return true;
            }

            value = default;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetFastTask<T>(ReadOnlySpan<char> cacheKey, [NotNullWhen(true)] out Task<T>? task)
        {
            if (TryGetFastNode<T>(cacheKey, out var value, out var node))
            {
                task = node is ValueNode<T> typed ? typed.AsCompletedTask() : Task.FromResult(value!);
                return true;
            }

            task = null;
            return false;
        }

        private bool TryGetFastNode<T>(string cacheKey, out T? value, out Node? node)
        {
            var now = _engine.Now;
            return AcceptFastNode(_options.EnableFastPath && _engine.TryGet(cacheKey, now, out var found) ? found : null, now, out value, out node);
        }

        private bool TryGetFastNode<T>(ReadOnlySpan<char> cacheKey, out T? value, out Node? node)
        {
            var now = _engine.Now;
            return AcceptFastNode(_options.EnableFastPath && _engine.TryGet(cacheKey, now, out var found) ? found : null, now, out value, out node);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetGeneratedTask<T>(ref MethodCache.Generated.KeyBuilder key, PolicyGeneration generation, string name, [NotNullWhen(true)] out Task<T>? task)
        {
            if (TryGetGeneratedNode<T>(ref key, generation, out var value, out var node))
            {
                RecordHitTelemetry(name, node);
                task = node is ValueNode<T> typed ? typed.AsCompletedTask() : Task.FromResult(value!);
                return true;
            }
            task = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetGeneratedTextTask<T>(string key, PolicyGeneration generation, string name, [NotNullWhen(true)] out Task<T>? task)
        {
            var now = _engine.Now;
            if (AcceptFastNode(_options.EnableFastPath && _engine.TryGet(key, now, out var found) ? found : null,
                now, out T? value, out var node, generation))
            {
                RecordHitTelemetry(name, node);
                task = node is ValueNode<T> typed ? typed.AsCompletedTask() : Task.FromResult(value!);
                return true;
            }
            task = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetGeneratedTextValue<T>(string key, PolicyGeneration generation, string name, out T? value)
        {
            var now = _engine.Now;
            if (!AcceptFastNode(_options.EnableFastPath && _engine.TryGet(key, now, out var found) ? found : null,
                now, out value, out var node, generation)) return false;
            RecordHitTelemetry(name, node);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetGeneratedValue<T>(ref MethodCache.Generated.KeyBuilder key, PolicyGeneration generation, string name, out T? value)
        {
            if (!TryGetGeneratedNode(ref key, generation, out value, out var node)) return false;
            RecordHitTelemetry(name, node);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetGeneratedNode<T>(ref MethodCache.Generated.KeyBuilder key, PolicyGeneration generation, out T? value, out Node? node)
        {
            var now = _engine.Now;
            Node? found = null;
            if (_options.EnableFastPath)
            {
                if (key.StoredText is { } text) _engine.TryGet(text, now, out found);
                else _engine.TryGet(key.AsSpan(), now, out found);
            }
            return AcceptFastNode(found, now, out value, out node, generation);
        }

        private bool AcceptFastNode<T>(Node? found, long now, out T? value, out Node? node, PolicyGeneration? generation = null)
        {
            if (TryAcceptFresh(found, now, out value, out node, out var refreshDue, generation)
                && !(refreshDue && Volatile.Read(ref node!.RefreshState) == 0))
            {
                if (_options.EnableStatistics)
                {
                    _hits.Increment();
                }

                return true;
            }

            value = default;
            node = null;
            return false;
        }

        /// <inheritdoc />
        public Task InvalidateByKeysAsync(params string[] keys)
        {
            if (keys == null)
            {
                return Task.CompletedTask;
            }

            using var activity = _distributed == null ? _telemetry?.BeginInvalidation("key", keys: keys) : null;
            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    RemoveLocal(key);
                }
            }

            _telemetry?.Invalidations("key", "local", keys.Where(static key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).Count());
            return _distributed == null
                ? Task.CompletedTask
                : _distributed.RemoveAsync(keys.Where(static key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray());
        }

        /// <inheritdoc />
        public Task InvalidateByTagsAsync(params string[] tags)
        {
            if (tags == null || tags.Length == 0)
            {
                return Task.CompletedTask;
            }

            using var activity = _distributed == null ? _telemetry?.BeginInvalidation("tag", tags: tags) : null;
            // Flights are flagged before entries are removed; see StoreLoaded.
            InvalidateFlights(flightTag => Array.IndexOf(tags, flightTag) >= 0);
            foreach (var tag in tags)
            {
                if (tag != null)
                {
                    _engine.RemoveByTag(tag);
                }
            }

            _telemetry?.Invalidations("tag", "local", tags.Where(static tag => tag != null).Distinct(StringComparer.Ordinal).Count());
            return _distributed == null
                ? Task.CompletedTask
                : _distributed.InvalidateTagsAsync(tags.Where(static tag => tag != null).Distinct(StringComparer.Ordinal).ToArray());
        }

        /// <inheritdoc />
        public Task InvalidateByTagPatternAsync(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return Task.CompletedTask;
            }

            if (_distributed != null)
            {
                // The distributed cache cannot list the tags it holds, so a pattern would only clear this node's memory
                throw new NotSupportedException(
                    "Tag pattern invalidation is not supported with a distributed cache. Invalidate the exact tags instead.");
            }

            InvalidateFlights(flightTag => WildcardPattern.IsMatch(pattern, flightTag));
            foreach (var tag in _engine.Tags)
            {
                if (WildcardPattern.IsMatch(pattern, tag))
                {
                    _engine.RemoveByTag(tag);
                }
            }

            return Task.CompletedTask;
        }

        #endregion

        #region IMemoryCache

        /// <inheritdoc />
        public ValueTask<T?> GetAsync<T>(string key)
        {
            if (TryGetFresh<T>(key, _engine.Now, out var value, out _, out _))
            {
                if (_options.EnableStatistics)
                {
                    _hits.Increment();
                }

                return new ValueTask<T?>(value);
            }

            if (_options.EnableStatistics)
            {
                _misses.Increment();
            }

            return new ValueTask<T?>(default(T));
        }

        /// <inheritdoc />
        public Task SetAsync<T>(string key, T value, TimeSpan expiration)
        {
            ArgumentNullException.ThrowIfNull(key);

            // A load that started before this write must not overwrite it with an older result.
            InvalidateFlight(key);
            Store(key, value, EntryPolicy.Plain, duration: Math.Max(1, ToNanos(expiration)));

            return _distributed == null
                ? Task.CompletedTask
                : _distributed.WriteAsync(key, value, tags: null, _distributed.Now, expiration, replaces: true);
        }

        /// <inheritdoc />
        public ValueTask<bool> RemoveAsync(string key)
        {
            var removed = RemoveLocal(key);
            return _distributed == null
                ? new ValueTask<bool>(removed)
                : RemoveDistributedAsync(new[] { key }, removed);
        }

        /// <inheritdoc />
        public ValueTask<int> RemoveMultipleAsync(params string[] keys)
        {
            var removed = 0;
            foreach (var key in keys)
            {
                if (RemoveLocal(key))
                {
                    removed++;
                }
            }

            return _distributed == null
                ? new ValueTask<int>(removed)
                : RemoveDistributedAsync(keys, removed);
        }

        private async ValueTask<TResult> RemoveDistributedAsync<TResult>(string[] keys, TResult result)
        {
            await _distributed!.RemoveAsync(keys).ConfigureAwait(false);
            return result;
        }

        /// <inheritdoc />
        public ValueTask<bool> ExistsAsync(string key)
        {
            var now = _engine.Now;
            var exists = _engine.TryPeek(key, out var node) && !node.IsLogicallyExpired(now) && !node.IsPhysicallyExpired(now);
            return new ValueTask<bool>(exists);
        }

        /// <inheritdoc />
        public ValueTask ClearAsync()
        {
            using var activity = _distributed == null ? _telemetry?.BeginInvalidation("tag") : null;
            ClearLocal();
            _hits.Reset();
            _misses.Reset();
            _telemetry?.Invalidations("tag", "local", 1);
            return _distributed == null ? ValueTask.CompletedTask : new ValueTask(_distributed.ClearAsync());
        }

        /// <inheritdoc />
        public ValueTask<ICacheStats> GetStatsAsync()
        {
            var external = _telemetry?.Observe() ?? default;
            var stats = new CacheStats
            {
                Hits = _hits.Sum(),
                Misses = _misses.Sum(),
                Evictions = _options.EnableStatistics ? _engine.EvictionCount : 0,
                LocalMethodEntries = _engine.Count,
                TotalWeight = _engine.TotalWeight,
                LocalHttpEntries = external.LocalHttpEntries,
                LocalHttpBytes = external.LocalHttpBytes,
                ObservedDistributedEntries = external.ObservedDistributedEntries
            };

            return new ValueTask<ICacheStats>(stats);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _maintenanceTimer?.Dispose();
            _distributed?.Dispose();
            _engine.Clear();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Loading

        private Task<T> GetOrCreateCoreAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, CancellationToken cancellationToken, Flight? publication = null)
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (policy.Source?.Enabled == false || policy.Source?.Generation?.IsCurrent == false)
                return _telemetry?.Enabled == true ? _telemetry.BypassAsync(methodName, key, factory) : factory();

            if (TryGetFresh<T>(key, _engine.Now, out var value, out var node, out var refreshDue))
            {
                if (refreshDue)
                {
                    StartBackgroundRefresh(key, methodName, factory, policy, node!, cancellationToken);
                }

                RecordHit(methodName);
                if (publication == null) RecordHitTelemetry(methodName, node);
                return node is ValueNode<T> typed ? typed.AsCompletedTask() : Task.FromResult(value!);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<T>(cancellationToken);
            }

            return LoadAsync(key, methodName, factory, policy, cancellationToken, publication);
        }

        /// <summary>
        /// Loads a missing entry, or joins the load another caller already started for the key.
        /// </summary>
        /// <remarks>
        /// <paramref name="factory"/> captures its caller's cancellation token, so a load can only be cancelled by
        /// the caller that started it. Callers that joined wait with their own token; when the load they joined was
        /// cancelled by its own caller, they load again instead of failing.
        /// </remarks>
        private async Task<T> LoadAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, CancellationToken cancellationToken, Flight? publication)
        {
            var outcome = RequestResult.Error;
            try
            {
                Flight flight;
                Node? previous;
                while (true)
                {
                    // The fail-safe fallback is bound to the entry that preceded this load, never to one written during it.
                    previous = policy.FailSafe > 0 && _engine.TryPeek(key, out var peeked) ? peeked : null;

                    flight = new Flight(policy.Tags, publication) { Generation = policy.Source?.Generation, MethodId = policy.Source?.MethodId };
                    var existing = JoinOrStartFlight(key, flight);
                    if (ReferenceEquals(existing, flight))
                    {
                        // A load that finished after this caller's miss (or after the load it joined was cancelled) stored
                        // its value before it left: use that value instead of running the factory a second time. The flight
                        // leaves before it completes, so a caller arriving after a concurrent invalidation of that value
                        // starts its own load instead of joining a finished flight.
                        if (TryGetFresh<T>(key, _engine.Now, out var loaded, out var loadedNode, out _))
                        {
                            _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
                            flight.Complete(loaded);
                            RecordHit(methodName);
                            outcome = HitResult(loadedNode);
                            return loaded!;
                        }

                        break;
                    }

                    // Another caller is already loading this key: share its result. A factory that asks for its own key
                    // would wait for itself forever, holding the key for every later caller, so it fails instead.
                    if (LoadingScope.Contains(existing))
                    {
                        throw new InvalidOperationException(
                            $"The factory of '{methodName}' requested the key it is loading, so it would wait for itself. " +
                            "Call the underlying implementation instead of the cached one inside the factory.");
                    }

                    object? shared;
                    try
                    {
                        if (policy.FailSafeTimeout > 0 && previous != null)
                        {
                            // Like the caller that started the load, don't wait past the timeout when a fail-safe value exists
                            try
                            {
                                shared = await existing.Task.WaitAsync(FromNanos(policy.FailSafeTimeout), _timeProvider, cancellationToken).ConfigureAwait(false);
                            }
                            catch (TimeoutException)
                            {
                                if (!existing.Invalidated && TryServeStale<T>(previous, policy, out var stale))
                                {
                                    RecordHit(methodName);
                                    outcome = RequestResult.Stale;
                                    return stale!;
                                }

                                shared = await existing.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            shared = await existing.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (existing.CancelledByCaller && !cancellationToken.IsCancellationRequested)
                    {
                        // The caller that started the load cancelled it; this caller still wants the value
                        continue;
                    }

                    RecordHit(methodName);
                    outcome = existing.Outcome == RequestResult.Stale ? RequestResult.Stale : RequestResult.Hit;
                    if (shared is T typed)
                    {
                        return typed;
                    }

                    if (shared == null && default(T) == null)
                    {
                        return default!;
                    }

                    // Same key used with a different result type: load independently.
                    outcome = RequestResult.Miss;
                    return await InvokeOriginAsync(key, methodName, factory, policy.Tags).ConfigureAwait(false);
                }

                // A load that may go on after its caller got the fail-safe value runs in its own DI scope from the start
                var load = RunLoadAsync(
                    key,
                    methodName,
                    policy.FailSafeTimeout > 0 && previous != null ? () => BackgroundLoadScope.RunAsync(factory) : factory,
                    policy,
                    flight,
                    previous,
                    cancellationToken);
                if (cancellationToken.CanBeCanceled && !load.IsCompleted)
                {
                    // This caller may stop waiting while an uncooperative factory still finishes later.
                    _ = load.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if (policy.FailSafeTimeout > 0 && previous != null && !load.IsCompleted)
                {
                    try
                    {
                        var value = await load.WaitAsync(FromNanos(policy.FailSafeTimeout), _timeProvider, cancellationToken).ConfigureAwait(false);
                        outcome = flight.Outcome;
                        return value;
                    }
                    catch (TimeoutException)
                    {
                        // Too slow: this caller gets the fail-safe value now and the load finishes in the background,
                        // storing its result (or serving the fail-safe value to callers that joined it) as usual.
                        if (!flight.Invalidated && TryServeStale<T>(previous, policy, out var stale))
                        {
                            _ = load.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            outcome = RequestResult.Stale;
                            return stale!;
                        }
                    }
                }

                var result = await load.WaitAsync(cancellationToken).ConfigureAwait(false);
                outcome = flight.Outcome;
                return result;
            }
            catch
            {
                outcome = RequestResult.Error;
                throw;
            }
            finally { if (publication == null) _telemetry?.Request("method", methodName, outcome); }
        }

        /// <summary>
        /// Runs the load this caller started for <paramref name="flight"/>: reads the distributed cache, else calls
        /// the factory, stores the result, and completes the flight for callers that joined it.
        /// </summary>
        private async Task<T> RunLoadAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, Flight flight, Node? previous, CancellationToken cancellationToken)
        {
            try
            {
                if (_distributed != null && policy.Source?.Distributed != false && policy.Source?.Generation?.DistributedReady != false && !flight.Invalidated)
                {
                    var stored = await ReadDistributedAsync<T>(key, policy, cancellationToken).ConfigureAwait(false);
                    if (stored.Found && Remaining(stored, policy) is > 0 and var remaining && ShouldStore(stored.Value, policy, methodName))
                    {
                        StoreLoaded(key, stored.Value, policy, flight, duration: remaining);
                        flight.Outcome = RequestResult.Hit;
                        flight.Complete(stored.Value);
                        RecordHit(methodName);
                        return stored.Value!;
                    }
                }

                RecordMiss(methodName);
                flight.Outcome = RequestResult.Miss;

                var loadStartedAt = flight.SourceStartedAt ?? _distributed?.Now ?? 0;
                T result;
                using (var origin = flight.IsBulkPublication ? null : _telemetry?.BeginLoad("method", methodName, key, policy.Tags))
                {
                    try { result = await LoadingScope.Run(flight, factory).ConfigureAwait(false); }
                    catch (Exception error) { origin?.Fail(error); throw; }
                }

                if (ShouldStore(result, policy, methodName))
                {
                    StoreLoaded(key, result, policy, flight);
                    await WriteDistributedAsync(key, result, policy, methodName, flight, loadStartedAt).ConfigureAwait(false);
                }

                flight.Complete(result);
                return result;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // The caller gave up; that says nothing about the dependency, so no error and no fail-safe value
                flight.CancelByCaller(ex);
                throw;
            }
            catch (Exception ex)
            {

                if (previous != null && !flight.Invalidated && TryServeStale<T>(previous, policy, out var stale))
                {
                    flight.Outcome = RequestResult.Stale;
                    flight.Complete(stale);
                    return stale!;
                }

                flight.Fail(ex);
                throw;
            }
            finally
            {
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
            }
        }

        private Task<T> InvokeOriginAsync<T>(string key, string name, Func<Task<T>> factory, string[]? tags)
            => _telemetry?.Enabled == true ? _telemetry.OriginAsync(name, key, tags, factory) : factory();

        private ValueTask<DistributedEntry<T>> ReadDistributedAsync<T>(string key, EntryPolicy policy, CancellationToken cancellationToken)
            => _distributed!.TryGetAsync<T>(key, cancellationToken, policy.Source?.Identity);

        private long Remaining<T>(DistributedEntry<T> entry, EntryPolicy policy)
        {
            var receiverLimit = Math.Min(policy.Duration ?? _defaultDuration, _maxDuration);
            var elapsedMilliseconds = Math.Max(0, _distributed!.Now - entry.CreatedAt);
            var elapsed = elapsedMilliseconds > MaxNanos / 1_000_000 ? MaxNanos : elapsedMilliseconds * 1_000_000;
            return Math.Min(ToNanos(entry.RemainingLifetime), receiverLimit - elapsed);
        }

        /// <summary>
        /// Looks up a key missing from memory in the distributed cache without running a factory, and stores a hit in
        /// memory as a load would. Not found when there is no distributed cache, when it doesn't hold the key, or when
        /// another caller is already loading the key (a later load joins that one).
        /// </summary>
        internal async ValueTask<(bool Found, T? Value)> TryGetDistributedAsync<T>(string key, string methodName, CacheRuntimePolicy policy, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(policy);
            if (_distributed == null || policy.Enabled == false || policy.Distributed == false || policy.Generation?.DistributedReady == false || policy.Generation?.IsCurrent == false)
            {
                return (false, default);
            }

            var entryPolicy = GetEntryPolicy(policy);
            var flight = new Flight(entryPolicy.Tags) { Generation = policy.Generation, MethodId = policy.MethodId };
            if (!ReferenceEquals(JoinOrStartFlight(key, flight), flight))
            {
                return (false, default);
            }

            var completed = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A load that finished since the caller's miss stored its value already
                if (TryGetFresh<T>(key, _engine.Now, out var loaded, out var loadedNode, out _))
                {
                    flight.Complete(loaded);
                    completed = true;
                    RecordHit(methodName);
                    return (true, loaded);
                }

                var stored = await ReadDistributedAsync<T>(key, entryPolicy, cancellationToken).ConfigureAwait(false);
                if (stored.Found && Remaining(stored, entryPolicy) is > 0 and var remaining && ShouldStore(stored.Value, entryPolicy, methodName))
                {
                    StoreLoaded(key, stored.Value, entryPolicy, flight, duration: remaining);
                    flight.Complete(stored.Value);
                    completed = true;
                    RecordHit(methodName);
                    return (true, stored.Value);
                }

                return (false, default);
            }
            finally
            {
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));

                // No value to share: callers that joined load again, as after a load its caller cancelled
                if (!completed)
                {
                    flight.CancelByCaller(new OperationCanceledException());
                }
            }
        }

        /// <summary>Registers <paramref name="flight"/> for the key, or returns the load already running for it.</summary>
        private Flight JoinOrStartFlight(string key, Flight flight)
        {
            if (flight.Generation?.IsCurrent == false) return flight;
            var existing = JoinOrStartCurrentFlight(key, flight);
            // Retirement may race dictionary admission. A retired owner becomes Invalidated and is replaceable;
            // a retired joiner must not join a newer generation. Publication still holds the generation gate.
            return !ReferenceEquals(existing, flight) && flight.Generation?.IsCurrent == false ? flight : existing;
        }

        private Flight JoinOrStartCurrentFlight(string key, Flight flight)
        {
            if (_syncFlights.TryGetValue(key, out var synchronous) && !synchronous.Invalidated) return synchronous;
            while (true)
            {
                var existing = _flights.GetOrAdd(key, flight);
                if (ReferenceEquals(existing, flight) || !existing.Invalidated)
                {
                    return existing;
                }

                // The running load was invalidated, so its result predates the invalidation: start a fresh one.
                // The old load only removes its own flight, so replacing it here is safe.
                if (_flights.TryUpdate(key, flight, existing))
                {
                    return flight;
                }
            }
        }

        private void StartBackgroundRefresh<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, Node node, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref node.RefreshState, 1, 0) != 0)
            {
                return;
            }

            var flight = new Flight(policy.Tags) { Generation = policy.Source?.Generation, MethodId = policy.Source?.MethodId };
            if (!_flights.TryAdd(key, flight))
            {
                // A load for this key is already running. Let a later hit try again if this node outlives it.
                Volatile.Write(ref node.RefreshState, 0);
                return;
            }

            RunDetached(async () =>
            {
                try
                {
                    var loadStartedAt = _distributed?.Now ?? 0;

                    // A scoped or transient service gets its own instance from a new scope: the request that found
                    // the entry due may end, and dispose its own instance, before the refresh does
                    T result;
                    using (var origin = _telemetry?.BeginLoad("method", methodName, key, policy.Tags))
                    {
                        try { result = await LoadingScope.Run(flight, () => BackgroundLoadScope.RunAsync(factory)).ConfigureAwait(false); }
                        catch (Exception error) { origin?.Fail(error); throw; }
                    }

                    // Replace only the node being refreshed, so a write or removal that slipped in before this
                    // refresh registered its flight is never overwritten or resurrected.
                    // A result CacheWhen rejects leaves the current value in place until it expires.
                    if (result == null && !_options.CacheNullValues) StoreLoaded(key, result, policy, flight, replacing: node);
                    if (ShouldStore(result, policy, methodName))
                    {
                        StoreLoaded(key, result, policy, flight, replacing: node);
                        await WriteDistributedAsync(key, result, policy, methodName, flight, loadStartedAt).ConfigureAwait(false);
                    }

                    flight.Complete(result);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    // The refresh runs the call that found the entry due, so it ends with that caller's request.
                    // The current value stays; callers that joined load again, and a later hit starts a new refresh.
                    Volatile.Write(ref node.RefreshState, 0);
                    flight.CancelByCaller(ex);
                }
                catch (Exception ex)
                {
                    // The current value stays until it expires; the next miss retries the factory.

                    // Callers that joined this refresh after the entry went logically stale get the fail-safe
                    // value, exactly as they would from a foreground load.
                    var now = _engine.Now;
                    T? stale = default;
                    var wasStale = node.IsLogicallyExpired(now);
                    var served = policy.FailSafe > 0 && !flight.Invalidated && (wasStale
                        // Joined callers get a stale value, so hold it for the throttle period like a foreground load.
                        ? TryServeStale(node, policy, out stale)
                        : node.IsAlive && !node.IsPhysicallyExpired(now) && TryRead(node, out stale));

                    if (served)
                    {
                        flight.Outcome = wasStale ? RequestResult.Stale : RequestResult.Hit;
                        flight.Complete(stale);
                    }
                    else
                    {
                        flight.Fail(ex);
                    }
                }
                finally
                {
                    _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
                }
            });
        }

        /// <summary>
        /// Starts work that belongs to no caller, such as a background refresh, without the caller's execution
        /// context, so it does not inherit the trace or ambient state of whichever request triggered it.
        /// </summary>
        private static void RunDetached(Func<Task> work)
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                _ = Task.Run(work);
                return;
            }

            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(work);
            }
        }

        /// <summary>
        /// Runs the engine's periodic maintenance. A scheduled timer is rooted by the runtime, so it holds the engine
        /// only weakly and stops itself once the engine is gone: a manager nobody disposed doesn't keep its values
        /// alive. It is created without the caller's execution context, which would otherwise stay alive with it.
        /// </summary>
        private sealed class MaintenanceTimer
        {
            private readonly WeakReference<MemoryCacheEngine> _engine;
            private ITimer? _timer;

            private MaintenanceTimer(MemoryCacheEngine engine) => _engine = new WeakReference<MemoryCacheEngine>(engine);

            public static ITimer Start(TimeProvider timeProvider, MemoryCacheEngine engine, TimeSpan interval)
            {
                var state = new MaintenanceTimer(engine);
                if (ExecutionContext.IsFlowSuppressed())
                {
                    return state._timer = timeProvider.CreateTimer(Tick, state, interval, interval);
                }

                using (ExecutionContext.SuppressFlow())
                {
                    return state._timer = timeProvider.CreateTimer(Tick, state, interval, interval);
                }
            }

            private static void Tick(object? state)
            {
                var timer = (MaintenanceTimer)state!;
                if (timer._engine.TryGetTarget(out var engine))
                {
                    engine.Maintain();
                }
                else
                {
                    timer._timer?.Dispose();
                }
            }
        }

        /// <summary>
        /// Asks the method's <c>CacheWhen</c> predicate whether a factory result may be stored. A predicate that throws
        /// is reported and treated as "don't store", so a successful call never fails because of it.
        /// </summary>
        private bool ShouldStore<T>(T result, EntryPolicy policy, string methodName)
        {
            if (result == null && !_options.CacheNullValues) return false;
            if (policy.CacheWhen is not { } cacheWhen)
            {
                return true;
            }

            try
            {
                return cacheWhen(result);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Stores a factory result unless invalidated. Publication and the invalidation flag share a flight lock,
        /// so an invalidated load can never briefly publish a node or overwrite a newer write.
        /// </summary>
        private void StoreLoaded<T>(string key, T result, EntryPolicy policy, Flight flight, Node? replacing = null, long? duration = null)
        {
            if (flight.Invalidated) return;
            if (result == null && !_options.CacheNullValues)
            {
                lock (flight.Gate)
                {
                    lock (policy.Source?.Generation?.Gate ?? flight.Gate)
                    {
                        if (flight.Invalidated) return;
                        var current = replacing;
                        if (current == null && !_engine.TryPeek(key, out current)) return;
                        _engine.RemoveNode(current);
                    }
                }
                return;
            }
            Store(key, result, policy, duration, replacing, flight);
        }

        private Node? PublishLoaded(Node node, Node? replacing, Flight flight)
        {
            if (flight.Invalidated) return null;
            var stored = Publish(node, replacing);
            if (_distributed is { } distributed && stored != null) flight.PublishedAt ??= distributed.Now;
            return stored;
        }

        private Node? Store<T>(string key, T value, EntryPolicy policy, long? duration = null, Node? replacing = null, Flight? flight = null)
        {
            if (value == null && !_options.CacheNullValues)
            {
                _engine.Remove(key);
                return null;
            }

            var now = _engine.Now;
            var explicitDuration = duration ?? policy.Duration;
            var lifetime = explicitDuration ?? _defaultDuration;
            if (_jitter > 0 && duration == null)
            {
                lifetime += Random.Shared.NextInt64(_jitter);
            }

            lifetime = Math.Min(lifetime, _maxDuration); // after jitter, so MaxExpiration stays a hard upper bound
            var absoluteExpiresAt = now + _maxDuration;
            if (policy.Sliding > 0)
            {
                // An entry nobody reads expires after one sliding window; reads extend it (see TryGetFresh), but
                // never past its Duration. Without a Duration, only MaxExpiration bounds it.
                var cap = explicitDuration != null ? lifetime : _maxDuration;
                absoluteExpiresAt = now + cap;
                lifetime = Math.Min(cap, policy.Sliding);
            }

            if (duration.HasValue) absoluteExpiresAt = Math.Min(absoluteExpiresAt, now + duration.Value);
            var logicalExpiresAt = now + lifetime;
            var node = new ValueNode<T>(key, value)
            {
                CreatedAt = now,
                LogicalExpiresAt = logicalExpiresAt,
                PhysicalExpiresAt = Math.Min(logicalExpiresAt + policy.FailSafe, duration.HasValue ? now + duration.Value : now + _maxDuration),
                AbsoluteExpiresAt = absoluteExpiresAt,
                HasSourceDeadline = duration.HasValue,
                Tags = policy.Tags,
                Policy = policy,
                Weight = _weigher == null ? 1 : Math.Max(0, _weigher(key, value))
            };

            // The application weigher and clock run above, outside the lock: they may re-enter the cache.
            if (flight == null) return Publish(node, replacing);
            lock (flight.Gate)
            {
                if (policy.Source?.Generation is { } generation)
                    lock (generation.Gate)
                        return PublishLoaded(node, replacing, flight);
                return PublishLoaded(node, replacing, flight);
            }
        }

        private Node? Publish(Node node, Node? replacing)
        {
            if (replacing == null)
            {
                _engine.Add(node);
            }
            else if (!_engine.TryReplace(replacing, node))
            {
                return null;
            }

            return node;
        }

        private bool TryGetFresh<T>(string key, long now, out T? value, out Node? node, out bool refreshDue)
            => TryAcceptFresh(_engine.TryGet(key, now, out var found) ? found : null, now, out value, out node, out refreshDue);

        private bool TryAcceptFresh<T>(Node? found, long now, out T? value, out Node? node, out bool refreshDue, PolicyGeneration? generation = null)
        {
            refreshDue = false;
            var policy = found?.Policy as EntryPolicy;
            // A generated caller already checked its captured generation. Compare the entry's identity rather
            // than reading its retirement flag again. Unbound public lookups still check retirement directly.
            var entryGeneration = policy?.Generation;
            var retired = generation == null ? entryGeneration?.IsCurrent == false
                : entryGeneration != null && !ReferenceEquals(entryGeneration, generation);
            if (found is null || found.IsLogicallyExpired(now) ||
                retired || !TryRead(found, out value))
            {
                value = default;
                node = null;
                return false;
            }

            node = found;
            if (policy is { NeedsReadCheck: true })
            {
                if (policy.Sliding > 0)
                {
                    var extended = Math.Min(now + policy.Sliding, Volatile.Read(ref found.AbsoluteExpiresAt));
                    if (extended > Volatile.Read(ref found.LogicalExpiresAt))
                    {
                        Volatile.Write(ref found.LogicalExpiresAt, extended);
                        var physicalCap = found.HasSourceDeadline ? found.AbsoluteExpiresAt : found.CreatedAt + _maxDuration;
                        Volatile.Write(ref found.PhysicalExpiresAt, Math.Min(extended + policy.FailSafe, physicalCap));
                    }
                }

                refreshDue = IsRefreshDue(found, policy, now);
            }

            return true;
        }

        private bool TryServeStale<T>(Node node, EntryPolicy policy, out T? value)
        {
            // Only the entry still cached under the key: one removed, invalidated or replaced since it was looked up
            // must never come back as a fallback
            var now = _engine.Now;
            if (node.IsAlive && !node.IsPhysicallyExpired(now) &&
                node.Policy is not EntryPolicy { Generation.IsCurrent: false } && TryRead(node, out value))
            {
                // Serve the stale value for a short while before trying the factory again. Reads must not extend it
                // with sliding expiration, or the factory would never be tried again, and no early refresh may start
                // before then either (the refresh state stays set).
                var retryUntil = Math.Min(now + policy.FailSafeRetryAfter, Volatile.Read(ref node.PhysicalExpiresAt));
                Volatile.Write(ref node.IsStaleFallback, true);
                Volatile.Write(ref node.AbsoluteExpiresAt, retryUntil);
                Volatile.Write(ref node.LogicalExpiresAt, retryUntil);
                Interlocked.Exchange(ref node.RefreshState, 1);
                return true;
            }

            value = default;
            return false;
        }

        private static bool IsRefreshDue(Node node, EntryPolicy policy, long now)
        {
            if (policy.RefreshWindow <= 0)
            {
                return false;
            }

            // A window as long as the entry's lifetime would refresh on every hit; such a window (from policies
            // combined at runtime, which can't be rejected up front) covers the second half of the lifetime instead
            var expiresAt = Volatile.Read(ref node.LogicalExpiresAt);
            var lifetime = expiresAt - node.CreatedAt;
            var window = policy.RefreshWindow < lifetime ? policy.RefreshWindow : lifetime / 2;
            return now >= expiresAt - window;
        }

        private static bool TryRead<T>(Node node, out T? value)
        {
            if (node is ValueNode<T> typed)
            {
                value = typed.Value;
                return true;
            }

            if (node.BoxedValue is T converted)
            {
                value = converted;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Writes a loaded value to the distributed cache, unless the load was invalidated, so other servers can
        /// read it. This never throws and is bounded by the distributed cache's operation timeout. A removal of the
        /// key during the write is seen through the flight, and the written value is removed again.
        /// </summary>
        private Task WriteDistributedAsync<T>(string key, T result, EntryPolicy policy, string methodName, Flight flight, long loadStartedAt)
        {
            if (_distributed == null || policy.Source?.Distributed == false || flight.Invalidated || (result == null && !_options.CacheNullValues))
            {
                return Task.CompletedTask;
            }

            var lifetime = TimeSpan.FromTicks(Math.Min(policy.Duration ?? _defaultDuration, _maxDuration) / NanosPerTick);
            return _distributed.WriteAsync(key, result, policy.Tags, loadStartedAt, lifetime, () => flight.Invalidated, methodName: methodName, identity: policy.Source?.Identity, methodId: policy.Source?.Identity != null ? policy.Source.MethodId : null, publishedAt: flight.PublishedAt);
        }

        private bool RemoveLocal(string key)
        {
            InvalidateFlight(key);
            return _engine.Remove(key);
        }

        private void RemoveLocalTag(string tag)
        {
            // Flights are flagged before entries are removed; see StoreLoaded.
            InvalidateFlights(flightTag => string.Equals(flightTag, tag, StringComparison.Ordinal));
            _engine.RemoveByTag(tag);
        }

        private void ClearLocal()
        {
            InvalidateFlights(static _ => true, includeUntagged: true);
            _engine.Clear();
        }

        private void InvalidateFlights(Func<string, bool> tagMatches, bool includeUntagged = false)
        {
            foreach (var batch in _bulkLoads) batch.Key.InvalidateTags(tagMatches, includeUntagged);
            if (_flights.IsEmpty && _syncFlights.IsEmpty)
            {
                return;
            }

            foreach (var flight in _flights.Values.Concat(_syncFlights.Values))
            {
                if (includeUntagged)
                {
                    flight.Invalidated = true;
                    continue;
                }

                if (flight.Tags == null)
                {
                    continue;
                }

                foreach (var tag in flight.Tags)
                {
                    if (tagMatches(tag))
                    {
                        flight.Invalidated = true;
                        break;
                    }
                }
            }
        }

        private static RequestResult HitResult(Node? node)
            => node != null && Volatile.Read(ref node.IsStaleFallback) ? RequestResult.Stale : RequestResult.Hit;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RecordHitTelemetry(string name, Node? node)
        {
            if (_telemetry?.RequestsEnabled == true) _telemetry.Request("method", name, HitResult(node));
        }

        private void RecordHit(string methodName)
        {
            if (_options.EnableStatistics)
            {
                _hits.Increment();
            }
        }

        private void RecordMiss(string methodName)
        {
            if (_options.EnableStatistics)
            {
                _misses.Increment();
            }
        }

        #endregion

        #region Helpers

        private static EntryPolicy GetEntryPolicy(CacheRuntimePolicy policy)
        {
            // Consecutive calls on a thread usually share a policy; the thread-local check skips the table lookup.
            var last = t_lastEntryPolicy;
            if (last != null && ReferenceEquals(last.Source, policy))
            {
                return last;
            }

            return t_lastEntryPolicy = PolicyCache.GetValue(policy, static p => EntryPolicy.From(p));
        }

        /// <summary>Converts to nanoseconds, saturating so that e.g. <see cref="TimeSpan.MaxValue"/> means "practically forever".</summary>
        private static long ToNanos(TimeSpan value) => value.Ticks >= MaxNanos / NanosPerTick ? MaxNanos : value.Ticks * NanosPerTick;

        private static TimeSpan FromNanos(long nanos) => TimeSpan.FromTicks(nanos / NanosPerTick);

        private void InvalidateFlight(string key)
        {
            if (_syncFlights.TryGetValue(key, out var synchronous)) synchronous.Invalidated = true;
            foreach (var batch in _bulkLoads) batch.Key.InvalidateKey(key);
            if (_flights.TryGetValue(key, out var flight))
            {
                flight.Invalidated = true;
            }
        }

        private static Func<long> CreateClock(TimeProvider? timeProvider)
        {
            if (timeProvider == null || ReferenceEquals(timeProvider, TimeProvider.System))
            {
                // Monotonic, millisecond resolution and the cheapest clock available.
                return static () => Environment.TickCount64 * 1_000_000;
            }

            // Measure from construction: absolute timestamps (e.g. UTC ticks) would overflow when scaled to nanoseconds.
            var nanosPerTimestamp = 1_000_000_000d / timeProvider.TimestampFrequency;
            var origin = timeProvider.GetTimestamp();
            return () => (long)((timeProvider.GetTimestamp() - origin) * nanosPerTimestamp);
        }

        private static EngineEvictionMode ToEngineMode(MemoryCacheEvictionPolicy policy) => policy switch
        {
            MemoryCacheEvictionPolicy.Lru => EngineEvictionMode.Lru,
            MemoryCacheEvictionPolicy.Fifo => EngineEvictionMode.Fifo,
            _ => EngineEvictionMode.TinyLfu
        };

        /// <summary>
        /// Per-method settings resolved once from <see cref="CacheRuntimePolicy"/>. Times are in nanoseconds.
        /// </summary>
        private sealed class EntryPolicy
        {
            /// <summary>No tags, sliding or refresh: used by <see cref="SetAsync{T}"/>, which supplies its own duration.</summary>
            public static readonly EntryPolicy Plain = new();

            public long? Duration { get; init; }

            public long Sliding { get; init; }

            public long RefreshWindow { get; init; }

            public string[]? Tags { get; init; }

            public Func<object?, bool>? CacheWhen { get; init; }

            /// <summary>How long an expired value is kept as a fail-safe fallback; zero when fail-safe is off.</summary>
            public long FailSafe { get; init; }

            /// <summary>How long a caller waits for a reload when a fail-safe value exists; zero waits for it.</summary>
            public long FailSafeTimeout { get; init; }

            public long FailSafeRetryAfter { get; init; } = DefaultFailSafeRetryAfter;

            /// <summary>The policy this was built from. The weak table does not let this back-reference keep the key alive.</summary>
            public CacheRuntimePolicy? Source { get; init; }
            public PolicyGeneration? Generation { get; init; }

            public bool NeedsReadCheck => Sliding > 0 || RefreshWindow > 0;

            private const long DefaultFailSafeRetryAfter = 30_000_000_000; // 30 seconds in nanoseconds

            private static long Positive(TimeSpan? value) => value is { } span && span > TimeSpan.Zero ? ToNanos(span) : 0;

            public static EntryPolicy From(CacheRuntimePolicy policy)
            {
                var refreshWindow = policy.RefreshAhead is { } refreshAhead && refreshAhead > TimeSpan.Zero
                    ? ToNanos(refreshAhead)
                    : 0;

                return new EntryPolicy
                {
                    Source = policy,
                    Generation = policy.Generation,
                    Duration = policy.Duration is { } duration && duration > TimeSpan.Zero ? ToNanos(duration) : null,
                    Sliding = policy.SlidingExpiration is { } sliding && sliding > TimeSpan.Zero ? ToNanos(sliding) : 0,
                    RefreshWindow = refreshWindow,
                    CacheWhen = policy.CacheWhen,
                    FailSafe = Positive(policy.FailSafe),
                    FailSafeTimeout = Positive(policy.FailSafeTimeout),
                    FailSafeRetryAfter = Positive(policy.FailSafeRetryAfter) is > 0 and var retryAfter ? retryAfter : DefaultFailSafeRetryAfter,
                    Tags = policy.Tags.Count > 0 ? policy.Tags.Distinct(StringComparer.Ordinal).ToArray() : null
                };
            }
        }

        /// <summary>
        /// The loads whose factories the current asynchronous call runs inside, innermost first, so a factory that
        /// requests a key it is itself loading is recognized instead of waiting for itself.
        /// </summary>
        private sealed class LoadingScope
        {
            private static readonly AsyncLocal<LoadingScope?> Current = new();

            private readonly Flight _flight;
            private readonly LoadingScope? _outer;

            private LoadingScope(Flight flight, LoadingScope? outer)
            {
                _flight = flight;
                _outer = outer;
            }

            /// <summary>Calls <paramref name="factory"/> as part of <paramref name="flight"/>.</summary>
            public static T Run<T>(Flight flight, Func<T> factory)
            {
                var outer = Current.Value;
                Current.Value = new LoadingScope(flight, outer);
                try
                {
                    // The factory's continuations keep the scope; the caller gets its own back once the call returns
                    return factory();
                }
                finally
                {
                    Current.Value = outer;
                }
            }
            /// <summary>Whether the current call runs inside a factory of <paramref name="flight"/>.</summary>
            public static bool Contains(Flight flight)
            {
                for (var scope = Current.Value; scope != null; scope = scope._outer)
                {
                    if (ReferenceEquals(scope._flight, flight))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// One running load that other callers of the same key join. It is its own completion source, so a load
        /// allocates one object less.
        /// </summary>
        private class Flight : TaskCompletionSource<object?>
        {
            internal RequestResult Outcome { get; set; } = RequestResult.Miss;
            internal PolicyGeneration? Generation { get; init; }
            internal string? MethodId { get; init; }
            private volatile bool _invalidated;
            private readonly Flight? _publication;
            public object Gate { get; }
            // L2 timestamps are absent for memory-only flights. Keeping their nullable longs in
            // optional state avoids charging every L1 miss for distributed publication bookkeeping.
            private DistributedLoadTimes? _distributedTimes;
            public long? SourceStartedAt
            {
                get => _distributedTimes?.StartedAt;
                init { if (value.HasValue) _distributedTimes = new(value); }
            }
            internal long? PublishedAt
            {
                get => _distributedTimes?.PublishedAt;
                set { if (value.HasValue) (_distributedTimes ??= new(null)).PublishedAt = value; }
            }

            private sealed class DistributedLoadTimes(long? startedAt)
            {
                internal readonly long? StartedAt = startedAt;
                internal long? PublishedAt;
            }

            public Flight(string[]? tags, Flight? publication = null)
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
            {
                Tags = tags;
                _publication = publication;
                Gate = publication?.Gate ?? new object();
                SourceStartedAt = publication?.SourceStartedAt;
            }

            internal bool IsBulkPublication => _publication != null;

            public string[]? Tags { get; }

            /// <summary>Set when the key or one of its tags is invalidated while loading; the result is then not stored.</summary>
            public bool Invalidated
            {
                get => _invalidated || _publication?.Invalidated == true || Generation?.IsCurrent == false;
                set { lock (Gate) _invalidated = value; }
            }

            /// <summary>Set when the caller that started the load cancelled it; callers that joined load again.</summary>
            public bool CancelledByCaller { get; private set; }

            public void Complete(object? result) => TrySetResult(result);

            public void CancelByCaller(OperationCanceledException exception)
            {
                CancelledByCaller = true;
                TrySetCanceled(exception.CancellationToken);
            }

            public void Fail(Exception exception)
            {
                if (TrySetException(exception))
                {
                    _ = Task.Exception; // observed: no UnobservedTaskException when nobody joined
                }
            }
        }

        private sealed class SyncFlight : Flight
        {
            internal SyncFlight(string[]? tags, PolicyGeneration? generation) : base(tags) => Generation = generation;
            internal int OwnerThread { get; } = Environment.CurrentManagedThreadId;
            internal ManualResetEventSlim Ready { get; } = new(false);
            internal object? Value { get; set; }
            internal System.Runtime.ExceptionServices.ExceptionDispatchInfo? Error { get; set; }
        }

        /// <summary>A batch's publication guards. Individual loads remain free to populate its keys while the batch reads.</summary>
        internal sealed class BulkLoad : IDisposable
        {
            private readonly InMemoryCacheManager _owner;
            private readonly EntryPolicy _policy;
            private readonly Dictionary<string, Flight> _guards;

            internal BulkLoad(InMemoryCacheManager owner, IEnumerable<string> keys, CacheRuntimePolicy policy)
            {
                _owner = owner;
                _policy = GetEntryPolicy(policy);
                var startedAt = owner._distributed?.Now ?? 0;
                _guards = keys.Distinct(StringComparer.Ordinal).ToDictionary(static key => key,
                    _ => new Flight(_policy.Tags) { SourceStartedAt = startedAt, Generation = policy.Generation, MethodId = policy.MethodId }, StringComparer.Ordinal);
            }

            internal Task<T> GetOrCreateAsync<T>(ICacheManager manager, string key, Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                // Preserve the application's decorators (authorization, tracing, custom result processing) while
                // the underlying memory manager adopts the batch's publication guard. Async decorators keep it
                // in their captured context; the calling batch gets its previous context back immediately.
                var previous = CurrentBulkPublication.Value;
                CurrentBulkPublication.Value = new BulkPublication(_owner, key, _guards[key]);
                try { return manager.GetOrCreateFastAsync(key, CacheKeyLookup.MethodName, factory, _policy.Source!, cancellationToken); }
                finally { CurrentBulkPublication.Value = previous; }
            }

            internal void InvalidateKey(string key)
            {
                if (_guards.TryGetValue(key, out var guard)) guard.Invalidated = true;
            }

            internal void InvalidateTags(Func<string, bool> tagMatches, bool includeUntagged)
            {
                if (includeUntagged || (_policy.Tags?.Any(tagMatches) ?? false))
                    foreach (var guard in _guards.Values) guard.Invalidated = true;
            }

            public void Dispose() => _owner._bulkLoads.TryRemove(this, out _);
        }

        private sealed record BulkPublication(InMemoryCacheManager Owner, string Key, Flight Guard);

        #endregion
    }
}
