using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MethodCache.Core.Infrastructure.Utilities;
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
    public class InMemoryCacheManager : ICacheManager, IMemoryCache
    {
        private const long NanosPerTick = 100;

        /// <summary>Upper bound for any duration (~73 years); leaves headroom so sums of durations cannot overflow.</summary>
        private const long MaxNanos = long.MaxValue / 4;

        private static readonly ConditionalWeakTable<CacheRuntimePolicy, EntryPolicy> PolicyCache = new();

        [ThreadStatic]
        private static EntryPolicy? t_lastEntryPolicy;

        private readonly MemoryCacheEngine _engine;
        private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly MethodCacheMemoryOptions _options;
        private readonly MemoryUsageCalculator _memoryCalculator;
        private readonly Func<string, object?, long>? _weigher;
        private readonly Timer? _maintenanceTimer;
        private readonly StripedCounter _hits = new();
        private readonly StripedCounter _misses = new();

        private readonly long _defaultDuration;
        private readonly long _maxDuration;
        private readonly TimeProvider _timeProvider;
        private readonly long _jitter;
        private readonly bool _highResolutionTiming;
        private readonly DistributedCacheLayer? _distributed;

        /// <summary>Whether the distributed cache is in use now; null without one.</summary>
        internal async ValueTask<bool?> CheckDistributedCacheAsync(CancellationToken cancellationToken) =>
            _distributed == null ? null : await _distributed.CheckAvailableAsync(cancellationToken).ConfigureAwait(false);

        private int _disposed;

        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider, IOptions<MethodCacheMemoryOptions>? options = null)
            : this(metricsProvider, options, timeProvider: null)
        {
        }

        /// <summary>
        /// Creates a cache manager that reads time from <paramref name="timeProvider"/> (useful for tests).
        /// </summary>
        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider, IOptions<MethodCacheMemoryOptions>? options, TimeProvider? timeProvider)
            : this(metricsProvider, options, timeProvider, distributed: null)
        {
        }

        /// <summary>
        /// Creates a cache manager that keeps entries in memory and in <paramref name="distributed"/>, read on an
        /// in-memory miss before the factory runs.
        /// </summary>
        internal InMemoryCacheManager(ICacheMetricsProvider metricsProvider, IOptions<MethodCacheMemoryOptions>? options, TimeProvider? timeProvider, DistributedCacheLayer? distributed)
        {
            _metricsProvider = metricsProvider ?? throw new ArgumentNullException(nameof(metricsProvider));
            _options = options?.Value ?? new MethodCacheMemoryOptions();
            _memoryCalculator = new MemoryUsageCalculator(_options);
            _weigher = _options.Weigher;

            _defaultDuration = ToNanos(_options.DefaultExpiration);
            _maxDuration = ToNanos(_options.MaxExpiration);
            _timeProvider = timeProvider ?? TimeProvider.System;
            _jitter = ToNanos(_options.JitterMaxDuration);
            _highResolutionTiming = timeProvider == null || ReferenceEquals(timeProvider, TimeProvider.System);

            var maximum = _weigher != null ? _options.MaxMemoryBytes : _options.MaxItems;
            _engine = new MemoryCacheEngine(maximum, ToEngineMode(_options.EvictionPolicy), CreateClock(timeProvider), (int)Math.Min(_options.MaxItems, int.MaxValue));

            if (_options.EnableBackgroundCleanup)
            {
                _maintenanceTimer = new Timer(static state => ((MemoryCacheEngine)state!).Maintain(), _engine, _options.CleanupInterval, _options.CleanupInterval);
            }

            _distributed = distributed;
            _distributed?.Attach(
                onKeyInvalidated: key => RemoveLocal(key),
                onTagInvalidated: RemoveLocalTag,
                onCleared: ClearLocal,
                onError: ex => _metricsProvider.CacheError("DistributedCache", ex.Message));
        }

        /// <summary>The underlying engine; exposed to tests that need deterministic policy behavior.</summary>
        internal MemoryCacheEngine Engine => _engine;

        #region ICacheManager

        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(keyGenerator);

            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return GetOrCreateCoreAsync(key, methodName, factory, GetEntryPolicy(policy), CancellationToken.None);
        }

        public Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("Cache key is required", nameof(cacheKey));
            }

            ArgumentNullException.ThrowIfNull(policy);
            return GetOrCreateCoreAsync(cacheKey, methodName, factory, GetEntryPolicy(policy), cancellationToken);
        }

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(keyGenerator);

            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return new ValueTask<T?>(TryGetFresh<T>(key, _engine.Now, out var value, out _, out _) ? value : default);
        }

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

        private bool AcceptFastNode<T>(Node? found, long now, out T? value, out Node? node)
        {
            if (TryAcceptFresh(found, now, out value, out node, out var refreshDue)
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

        public Task InvalidateByKeysAsync(params string[] keys)
        {
            if (keys == null)
            {
                return Task.CompletedTask;
            }

            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    RemoveLocal(key);
                }
            }

            return _distributed == null
                ? Task.CompletedTask
                : _distributed.RemoveAsync(keys.Where(static key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray());
        }

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            if (tags == null || tags.Length == 0)
            {
                return Task.CompletedTask;
            }

            // Flights are flagged before entries are removed; see StoreLoaded.
            InvalidateFlights(flightTag => Array.IndexOf(tags, flightTag) >= 0);
            foreach (var tag in tags)
            {
                if (tag != null)
                {
                    _engine.RemoveByTag(tag);
                }
            }

            return _distributed == null
                ? Task.CompletedTask
                : _distributed.InvalidateTagsAsync(tags.Where(static tag => tag != null).Distinct(StringComparer.Ordinal).ToArray());
        }

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

        public ValueTask<bool> RemoveAsync(string key)
        {
            var removed = RemoveLocal(key);
            return _distributed == null
                ? new ValueTask<bool>(removed)
                : RemoveDistributedAsync(new[] { key }, removed);
        }

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

        public ValueTask<bool> ExistsAsync(string key)
        {
            var exists = _engine.TryPeek(key, out var node) && !node.IsLogicallyExpired(_engine.Now);
            return new ValueTask<bool>(exists);
        }

        public ValueTask ClearAsync()
        {
            ClearLocal();
            _hits.Reset();
            _misses.Reset();
            return _distributed == null ? ValueTask.CompletedTask : new ValueTask(_distributed.ClearAsync());
        }

        public ValueTask<ICacheStats> GetStatsAsync()
        {
            var stats = new CacheStats
            {
                Hits = _hits.Sum(),
                Misses = _misses.Sum(),
                Evictions = _options.EnableStatistics ? _engine.EvictionCount : 0,
                Entries = _engine.Count,
                MemoryUsage = _weigher != null
                    ? _engine.TotalWeight
                    : _memoryCalculator.CalculateMemoryUsage(_engine.Map, static node => node.BoxedValue!)
            };

            return new ValueTask<ICacheStats>(stats);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _maintenanceTimer?.Dispose();
            _engine.Clear();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Loading

        private Task<T> GetOrCreateCoreAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(factory);

            if (TryGetFresh<T>(key, _engine.Now, out var value, out var node, out var refreshDue))
            {
                if (refreshDue)
                {
                    StartBackgroundRefresh(key, methodName, factory, policy, node!, cancellationToken);
                }

                RecordHit(methodName);
                return node is ValueNode<T> typed ? typed.AsCompletedTask() : Task.FromResult(value!);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<T>(cancellationToken);
            }

            return LoadAsync(key, methodName, factory, policy, cancellationToken);
        }

        /// <summary>
        /// Loads a missing entry, or joins the load another caller already started for the key.
        /// </summary>
        /// <remarks>
        /// <paramref name="factory"/> captures its caller's cancellation token, so a load can only be cancelled by
        /// the caller that started it. Callers that joined wait with their own token; when the load they joined was
        /// cancelled by its own caller, they load again instead of failing.
        /// </remarks>
        private async Task<T> LoadAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, CancellationToken cancellationToken)
        {
            Flight flight;
            Node? previous;
            while (true)
            {
                // The fail-safe fallback is bound to the entry that preceded this load, never to one written during it.
                previous = policy.FailSafe > 0 && _engine.TryPeek(key, out var peeked) ? peeked : null;

                flight = new Flight(policy.Tags);
                var existing = JoinOrStartFlight(key, flight);
                if (ReferenceEquals(existing, flight))
                {
                    break;
                }

                // Another caller is already loading this key: share its result.
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
                            if (TryServeStale<T>(previous, policy, out var stale))
                            {
                                RecordHit(methodName);
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
                if (shared is T typed)
                {
                    return typed;
                }

                if (shared == null && default(T) == null)
                {
                    return default!;
                }

                // Same key used with a different result type: load independently.
                return await factory().ConfigureAwait(false);
            }

            var load = RunLoadAsync(key, methodName, factory, policy, flight, previous, cancellationToken);
            if (policy.FailSafeTimeout > 0 && previous != null && !load.IsCompleted)
            {
                try
                {
                    return await load.WaitAsync(FromNanos(policy.FailSafeTimeout), _timeProvider).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Too slow: this caller gets the fail-safe value now and the load finishes in the background,
                    // storing its result (or serving the fail-safe value to callers that joined it) as usual.
                    if (!flight.Invalidated && TryServeStale<T>(previous, policy, out var stale))
                    {
                        _ = load.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        return stale!;
                    }
                }
            }

            return await load.ConfigureAwait(false);
        }

        /// <summary>
        /// Runs the load this caller started for <paramref name="flight"/>: reads the distributed cache, else calls
        /// the factory, stores the result, and completes the flight for callers that joined it.
        /// </summary>
        private async Task<T> RunLoadAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, Flight flight, Node? previous, CancellationToken cancellationToken)
        {
            try
            {
                if (_distributed != null)
                {
                    var stored = await _distributed.TryGetAsync<T>(key).ConfigureAwait(false);
                    if (stored.Found)
                    {
                        StoreLoaded(key, stored.Value, policy, flight, duration: Math.Max(1, ToNanos(stored.RemainingLifetime)));
                        flight.Complete(stored.Value);
                        RecordHit(methodName);
                        return stored.Value!;
                    }
                }

                RecordMiss(methodName);

                var loadStartedAt = _distributed?.Now ?? 0;
                var start = StartTiming();
                var result = await factory().ConfigureAwait(false);
                var elapsed = ElapsedSince(start);

                if (ShouldStore(result, policy, methodName))
                {
                    StoreLoaded(key, result, policy, flight);
                    await WriteDistributedAsync(key, result, policy, flight, loadStartedAt).ConfigureAwait(false);
                }

                flight.Complete(result);
                RecordLoadLatency(methodName, elapsed);
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
                _metricsProvider.CacheError(methodName, ex.Message);

                if (previous != null && !flight.Invalidated && TryServeStale<T>(previous, policy, out var stale))
                {
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

        /// <summary>Registers <paramref name="flight"/> for the key, or returns the load already running for it.</summary>
        private Flight JoinOrStartFlight(string key, Flight flight)
        {
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

            var flight = new Flight(policy.Tags);
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
                    var start = StartTiming();
                    var result = await factory().ConfigureAwait(false);
                    var elapsed = ElapsedSince(start);

                    // Replace only the node being refreshed, so a write or removal that slipped in before this
                    // refresh registered its flight is never overwritten or resurrected.
                    // A result CacheWhen rejects leaves the current value in place until it expires.
                    if (ShouldStore(result, policy, methodName))
                    {
                        StoreLoaded(key, result, policy, flight, replacing: node);
                        await WriteDistributedAsync(key, result, policy, flight, loadStartedAt).ConfigureAwait(false);
                    }

                    flight.Complete(result);
                    RecordLoadLatency(methodName, elapsed);
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
                    _metricsProvider.CacheError(methodName, $"Background refresh failed: {ex.Message}");

                    // Callers that joined this refresh after the entry went logically stale get the fail-safe
                    // value, exactly as they would from a foreground load.
                    var now = _engine.Now;
                    T? stale = default;
                    var served = policy.FailSafe > 0 && (node.IsLogicallyExpired(now)
                        // Joined callers get a stale value, so hold it for the throttle period like a foreground load.
                        ? TryServeStale(node, policy, out stale)
                        : !node.IsPhysicallyExpired(now) && TryRead(node, out stale));

                    if (served)
                    {
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
        /// Asks the method's <c>CacheWhen</c> predicate whether a factory result may be stored. A predicate that throws
        /// is reported and treated as "don't store", so a successful call never fails because of it.
        /// </summary>
        private bool ShouldStore<T>(T result, EntryPolicy policy, string methodName)
        {
            if (policy.CacheWhen is not { } cacheWhen)
            {
                return true;
            }

            try
            {
                return cacheWhen(result);
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, $"CacheWhen threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stores a factory result unless its key or tags were invalidated while it loaded. Invalidators flag the
        /// flight before removing entries, so either they remove this node or the re-check below sees the flag.
        /// </summary>
        private void StoreLoaded<T>(string key, T result, EntryPolicy policy, Flight flight, Node? replacing = null, long? duration = null)
        {
            if (flight.Invalidated)
            {
                return;
            }

            if (result == null && !_options.CacheNullValues)
            {
                // Nothing to store, so drop the entry this load replaces. A write that invalidated the flight
                // before adding its node is seen by the re-check, so its newer entry is never removed.
                var current = replacing;
                if (current == null && !_engine.TryPeek(key, out current))
                {
                    return;
                }

                Interlocked.MemoryBarrier();
                if (!flight.Invalidated)
                {
                    _engine.RemoveNode(current);
                }

                return;
            }

            var node = Store(key, result, policy, duration, replacing);
            Interlocked.MemoryBarrier();
            if (node != null && flight.Invalidated)
            {
                _engine.RemoveNode(node);
            }
        }

        private Node? Store<T>(string key, T value, EntryPolicy policy, long? duration = null, Node? replacing = null)
        {
            if (value == null && !_options.CacheNullValues)
            {
                _engine.Remove(key);
                return null;
            }

            var now = _engine.Now;
            var explicitDuration = duration ?? policy.Duration;
            var lifetime = explicitDuration ?? _defaultDuration;
            if (_jitter > 0)
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

            var logicalExpiresAt = now + lifetime;
            var node = new ValueNode<T>(key, value)
            {
                CreatedAt = now,
                LogicalExpiresAt = logicalExpiresAt,
                PhysicalExpiresAt = logicalExpiresAt + policy.FailSafe,
                AbsoluteExpiresAt = absoluteExpiresAt,
                Tags = policy.Tags,
                Policy = policy,
                Weight = _weigher == null ? 1 : Math.Max(0, _weigher(key, value))
            };

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

        private bool TryAcceptFresh<T>(Node? found, long now, out T? value, out Node? node, out bool refreshDue)
        {
            refreshDue = false;

            if (found is null || found.IsLogicallyExpired(now) || !TryRead(found, out value))
            {
                value = default;
                node = null;
                return false;
            }

            node = found;
            if (found.Policy is EntryPolicy { NeedsReadCheck: true } policy)
            {
                if (policy.Sliding > 0)
                {
                    var extended = Math.Min(now + policy.Sliding, Volatile.Read(ref found.AbsoluteExpiresAt));
                    if (extended > Volatile.Read(ref found.LogicalExpiresAt))
                    {
                        Volatile.Write(ref found.LogicalExpiresAt, extended);
                        Volatile.Write(ref found.PhysicalExpiresAt, extended + policy.FailSafe);
                    }
                }

                refreshDue = IsRefreshDue(found, policy, now);
            }

            return true;
        }

        private bool TryServeStale<T>(Node node, EntryPolicy policy, out T? value)
        {
            var now = _engine.Now;
            if (!node.IsPhysicallyExpired(now) && TryRead(node, out value))
            {
                // Serve the stale value for a short while before trying the factory again. Reads must not extend it
                // with sliding expiration, or the factory would never be tried again.
                Volatile.Write(ref node.AbsoluteExpiresAt, now + policy.FailSafeRetryAfter);
                Volatile.Write(ref node.LogicalExpiresAt, now + policy.FailSafeRetryAfter);
                Interlocked.Exchange(ref node.RefreshState, 0);
                return true;
            }

            value = default;
            return false;
        }

        private static bool IsRefreshDue(Node node, EntryPolicy policy, long now)
        {
            var expiresAt = Volatile.Read(ref node.LogicalExpiresAt);

            if (policy.RefreshWindow > 0 && now >= expiresAt - policy.RefreshWindow)
            {
                return true;
            }

            return false;
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
        private Task WriteDistributedAsync<T>(string key, T result, EntryPolicy policy, Flight flight, long loadStartedAt)
        {
            if (_distributed == null || flight.Invalidated || (result == null && !_options.CacheNullValues))
            {
                return Task.CompletedTask;
            }

            var lifetime = TimeSpan.FromTicks(Math.Min(policy.Duration ?? _defaultDuration, _maxDuration) / NanosPerTick);
            return _distributed.WriteAsync(key, result, policy.Tags, loadStartedAt, lifetime, () => flight.Invalidated);
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
            if (_flights.IsEmpty)
            {
                return;
            }

            foreach (var pair in _flights)
            {
                var flight = pair.Value;
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

        private void RecordHit(string methodName)
        {
            _metricsProvider.CacheHit(methodName);
            if (_options.EnableStatistics)
            {
                _hits.Increment();
            }
        }

        /// <summary>Reports how long the cached method took to produce a value, in milliseconds.</summary>
        private void RecordLoadLatency(string methodName, long elapsedNanos)
        {
            _metricsProvider.CacheLatency(methodName, elapsedNanos / 1_000_000);
        }

        private void RecordMiss(string methodName)
        {
            _metricsProvider.CacheMiss(methodName);
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

        /// <summary>
        /// Factory timings feed the load-latency metric, which needs sub-millisecond resolution. The default engine
        /// clock ticks in milliseconds, so with the system clock a high-resolution timestamp is used instead.
        /// </summary>
        private long StartTiming() => _highResolutionTiming ? Stopwatch.GetTimestamp() : _engine.Now;

        private long ElapsedSince(long start) => _highResolutionTiming
            ? Stopwatch.GetElapsedTime(start).Ticks * NanosPerTick
            : _engine.Now - start;

        private void InvalidateFlight(string key)
        {
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
        /// One running load that other callers of the same key join. It is its own completion source, so a load
        /// allocates one object less.
        /// </summary>
        private sealed class Flight : TaskCompletionSource<object?>
        {
            private volatile bool _invalidated;

            public Flight(string[]? tags)
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
            {
                Tags = tags;
            }

            public string[]? Tags { get; }

            /// <summary>Set when the key or one of its tags is invalidated while loading; the result is then not stored.</summary>
            public bool Invalidated
            {
                get => _invalidated;
                set => _invalidated = value;
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

        #endregion
    }
}
