using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Infrastructure.Utilities;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Runtime.Execution
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
        private readonly MemoryCacheOptions _options;
        private readonly MemoryUsageCalculator _memoryCalculator;
        private readonly Func<string, object?, long>? _weigher;
        private readonly Timer? _maintenanceTimer;
        private readonly StripedCounter _hits = new();
        private readonly StripedCounter _misses = new();

        private readonly long _defaultDuration;
        private readonly long _maxDuration;
        private readonly long _failSafeDuration;
        private readonly long _failSafeThrottle;
        private readonly long _jitter;
        private readonly bool _highResolutionTiming;

        private int _disposed;

        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider, IOptions<MemoryCacheOptions>? options = null)
            : this(metricsProvider, options, timeProvider: null)
        {
        }

        /// <summary>
        /// Creates a cache manager that reads time from <paramref name="timeProvider"/> (useful for tests).
        /// </summary>
        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider, IOptions<MemoryCacheOptions>? options, TimeProvider? timeProvider)
        {
            _metricsProvider = metricsProvider ?? throw new ArgumentNullException(nameof(metricsProvider));
            _options = options?.Value ?? new MemoryCacheOptions();
            _memoryCalculator = new MemoryUsageCalculator(_options);
            _weigher = _options.Weigher;

            _defaultDuration = ToNanos(_options.DefaultExpiration);
            _maxDuration = ToNanos(_options.MaxExpiration);
            _failSafeDuration = _options.EnableFailSafe ? ToNanos(_options.FailSafeMaxDuration) : 0;
            _failSafeThrottle = ToNanos(_options.FailSafeThrottleDuration);
            _jitter = ToNanos(_options.JitterMaxDuration);
            _highResolutionTiming = timeProvider == null || ReferenceEquals(timeProvider, TimeProvider.System);

            var maximum = _weigher != null ? _options.MaxMemoryBytes : _options.MaxItems;
            _engine = new MemoryCacheEngine(maximum, ToEngineMode(_options.EvictionPolicy), CreateClock(timeProvider), (int)Math.Min(_options.MaxItems, int.MaxValue));

            if (_options.EnableBackgroundCleanup)
            {
                _maintenanceTimer = new Timer(static state => ((MemoryCacheEngine)state!).Maintain(), _engine, _options.CleanupInterval, _options.CleanupInterval);
            }
        }

        /// <summary>The underlying engine; exposed to tests that need deterministic policy behavior.</summary>
        internal MemoryCacheEngine Engine => _engine;

        #region ICacheManager

        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(keyGenerator);

            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return GetOrCreateCoreAsync(key, methodName, factory, GetEntryPolicy(policy));
        }

        public Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("Cache key is required", nameof(cacheKey));
            }

            ArgumentNullException.ThrowIfNull(policy);
            return GetOrCreateCoreAsync(cacheKey, methodName, factory, GetEntryPolicy(policy));
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
            if (_options.EnableFastPath
                && TryGetFresh(cacheKey, _engine.Now, out value, out var node, out var refreshDue)
                && !(refreshDue && Volatile.Read(ref node!.RefreshState) == 0))
            {
                if (_options.FastPathTrackMetrics && _options.EnableStatistics)
                {
                    _hits.Increment();
                }

                return true;
            }

            value = default;
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
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                InvalidateFlight(key);
                _engine.Remove(key);
            }

            return Task.CompletedTask;
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

            return Task.CompletedTask;
        }

        public Task InvalidateByTagPatternAsync(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return Task.CompletedTask;
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
            Store(key, value, EntryPolicy.Plain, computeDuration: 0, duration: Math.Max(1, ToNanos(expiration)));
            return Task.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key)
        {
            InvalidateFlight(key);
            return new ValueTask<bool>(_engine.Remove(key));
        }

        public ValueTask<int> RemoveMultipleAsync(params string[] keys)
        {
            var removed = 0;
            foreach (var key in keys)
            {
                InvalidateFlight(key);
                if (_engine.Remove(key))
                {
                    removed++;
                }
            }

            return new ValueTask<int>(removed);
        }

        public ValueTask<bool> ExistsAsync(string key)
        {
            var exists = _engine.TryPeek(key, out var node) && !node.IsLogicallyExpired(_engine.Now);
            return new ValueTask<bool>(exists);
        }

        public ValueTask ClearAsync()
        {
            InvalidateFlights(static _ => true, includeUntagged: true);
            _engine.Clear();
            _hits.Reset();
            _misses.Reset();
            return ValueTask.CompletedTask;
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

        private Task<T> GetOrCreateCoreAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(factory);

            if (TryGetFresh<T>(key, _engine.Now, out var value, out var node, out var refreshDue))
            {
                if (refreshDue)
                {
                    StartBackgroundRefresh(key, methodName, factory, policy, node!);
                }

                RecordHit(methodName);
                return node is ValueNode<T> typed ? typed.AsCompletedTask() : Task.FromResult(value!);
            }

            return LoadAsync(key, methodName, factory, policy);
        }

        private async Task<T> LoadAsync<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy)
        {
            // The fail-safe fallback is bound to the entry that preceded this load, never to one written during it.
            var previous = _failSafeDuration > 0 && _engine.TryPeek(key, out var peeked) ? peeked : null;

            var flight = new Flight(policy.Tags);
            Flight existing;
            while (true)
            {
                existing = _flights.GetOrAdd(key, flight);
                if (ReferenceEquals(existing, flight) || !existing.Invalidated)
                {
                    break;
                }

                // The running load was invalidated, so its result predates the invalidation: start a fresh one.
                // The old load only removes its own flight, so replacing it here is safe.
                if (_flights.TryUpdate(key, flight, existing))
                {
                    existing = flight;
                    break;
                }
            }

            if (!ReferenceEquals(existing, flight))
            {
                // Another caller is already loading this key: share its result.
                RecordHit(methodName);
                var shared = policy.LockTimeout is { } timeout
                    ? await existing.Task.WaitAsync(timeout).ConfigureAwait(false)
                    : await existing.Task.ConfigureAwait(false);

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

            RecordMiss(methodName);

            try
            {
                var start = StartTiming();
                var result = await factory().ConfigureAwait(false);

                StoreLoaded(key, result, policy, flight, ElapsedSince(start));
                flight.Complete(result);
                return result;
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, ex.Message);

                if (previous != null && !flight.Invalidated && TryServeStale<T>(previous, out var stale))
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

        private void StartBackgroundRefresh<T>(string key, string methodName, Func<Task<T>> factory, EntryPolicy policy, Node node)
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

            _ = Task.Run(async () =>
            {
                try
                {
                    var start = StartTiming();
                    var result = await factory().ConfigureAwait(false);

                    // Replace only the node being refreshed, so a write or removal that slipped in before this
                    // refresh registered its flight is never overwritten or resurrected.
                    StoreLoaded(key, result, policy, flight, ElapsedSince(start), replacing: node);
                    flight.Complete(result);
                }
                catch (Exception ex)
                {
                    // The current value stays until it expires; the next miss retries the factory.
                    _metricsProvider.CacheError(methodName, $"Background refresh failed: {ex.Message}");

                    // Callers that joined this refresh after the entry went logically stale get the fail-safe
                    // value, exactly as they would from a foreground load.
                    var now = _engine.Now;
                    T? stale = default;
                    var served = _failSafeDuration > 0 && (node.IsLogicallyExpired(now)
                        // Joined callers get a stale value, so hold it for the throttle period like a foreground load.
                        ? TryServeStale(node, out stale)
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
        /// Stores a factory result unless its key or tags were invalidated while it loaded. Invalidators flag the
        /// flight before removing entries, so either they remove this node or the re-check below sees the flag.
        /// </summary>
        private void StoreLoaded<T>(string key, T result, EntryPolicy policy, Flight flight, long computeDuration, Node? replacing = null)
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

            var node = Store(key, result, policy, computeDuration, replacing: replacing);
            Interlocked.MemoryBarrier();
            if (node != null && flight.Invalidated)
            {
                _engine.RemoveNode(node);
            }
        }

        private Node? Store<T>(string key, T value, EntryPolicy policy, long computeDuration, long? duration = null, Node? replacing = null)
        {
            if (value == null && !_options.CacheNullValues)
            {
                _engine.Remove(key);
                return null;
            }

            var now = _engine.Now;
            var lifetime = duration ?? policy.Duration ?? _defaultDuration;
            if (_jitter > 0)
            {
                lifetime += Random.Shared.NextInt64(_jitter);
            }

            lifetime = Math.Min(lifetime, _maxDuration); // after jitter, so MaxExpiration stays a hard upper bound
            if (policy.Sliding > 0)
            {
                // An entry nobody reads expires after one sliding window; reads extend it (see TryGetFresh).
                lifetime = Math.Min(lifetime, policy.Sliding);
            }

            var logicalExpiresAt = now + lifetime;
            var node = new ValueNode<T>(key, value)
            {
                CreatedAt = now,
                LogicalExpiresAt = logicalExpiresAt,
                PhysicalExpiresAt = logicalExpiresAt + _failSafeDuration,
                AbsoluteExpiresAt = now + _maxDuration,
                ComputeDuration = computeDuration,
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
        {
            refreshDue = false;

            if (!_engine.TryGet(key, now, out var found) || found.IsLogicallyExpired(now) || !TryRead(found, out value))
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
                    var extended = Math.Min(now + policy.Sliding, found.AbsoluteExpiresAt);
                    if (extended > Volatile.Read(ref found.LogicalExpiresAt))
                    {
                        Volatile.Write(ref found.LogicalExpiresAt, extended);
                        Volatile.Write(ref found.PhysicalExpiresAt, extended + _failSafeDuration);
                    }
                }

                refreshDue = IsRefreshDue(found, policy, now);
            }

            return true;
        }

        private bool TryServeStale<T>(Node node, out T? value)
        {
            var now = _engine.Now;
            if (!node.IsPhysicallyExpired(now) && TryRead(node, out value))
            {
                // Serve the stale value for a short while before trying the factory again.
                Volatile.Write(ref node.LogicalExpiresAt, now + _failSafeThrottle);
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

            if (policy.Beta > 0)
            {
                // XFetch (Vattani et al., "Optimal Probabilistic Cache Stampede Prevention"): refresh early with a
                // probability that rises as expiry approaches, scaled by how long the value takes to compute.
                var sample = Random.Shared.NextDouble();
                var delta = Math.Max(node.ComputeDuration, 1);
                return now - delta * policy.Beta * Math.Log(sample <= 0 ? double.Epsilon : sample) >= expiresAt;
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

        /// <summary>
        /// Factory timings feed XFetch, which needs sub-millisecond resolution. The default engine clock ticks in
        /// milliseconds, so with the system clock a high-resolution timestamp is used instead.
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
            MemoryCacheEvictionPolicy.LRU => EngineEvictionMode.Lru,
            MemoryCacheEvictionPolicy.FIFO => EngineEvictionMode.Fifo,
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

            public double Beta { get; init; }

            public TimeSpan? LockTimeout { get; init; }

            public string[]? Tags { get; init; }

            /// <summary>The policy this was built from. The weak table does not let this back-reference keep the key alive.</summary>
            public CacheRuntimePolicy? Source { get; init; }

            public bool NeedsReadCheck => Sliding > 0 || RefreshWindow > 0 || Beta > 0;

            public static EntryPolicy From(CacheRuntimePolicy policy)
            {
                var stampede = policy.StampedeProtection;

                var refreshWindow = policy.RefreshAhead is { } refreshAhead && refreshAhead > TimeSpan.Zero
                    ? ToNanos(refreshAhead)
                    : 0;
                if (stampede is { Mode: StampedeProtectionMode.RefreshAhead, RefreshAheadWindow: { } window } && window > TimeSpan.Zero)
                {
                    refreshWindow = Math.Max(refreshWindow, ToNanos(window));
                }

                TimeSpan? lockTimeout = policy.DistributedLock?.Timeout;
                if (lockTimeout == null && stampede?.Mode == StampedeProtectionMode.DistributedLock)
                {
                    lockTimeout = TimeSpan.FromSeconds(30);
                }

                if (lockTimeout <= TimeSpan.Zero)
                {
                    lockTimeout = TimeSpan.FromSeconds(30);
                }

                return new EntryPolicy
                {
                    Source = policy,
                    Duration = policy.Duration is { } duration && duration > TimeSpan.Zero ? ToNanos(duration) : null,
                    Sliding = policy.SlidingExpiration is { } sliding && sliding > TimeSpan.Zero ? ToNanos(sliding) : 0,
                    RefreshWindow = refreshWindow,
                    Beta = stampede?.Mode == StampedeProtectionMode.Probabilistic ? (stampede.Beta > 0 ? stampede.Beta : 1d) : 0,
                    LockTimeout = lockTimeout,
                    Tags = policy.Tags.Count > 0 ? policy.Tags.Distinct(StringComparer.Ordinal).ToArray() : null
                };
            }
        }

        /// <summary>An in-progress load that concurrent callers for the same key join.</summary>
        private sealed class Flight
        {
            private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private volatile bool _invalidated;

            public Flight(string[]? tags)
            {
                Tags = tags;
            }

            public string[]? Tags { get; }

            public Task<object?> Task => _completion.Task;

            /// <summary>Set when the key or one of its tags is invalidated while loading; the result is then not stored.</summary>
            public bool Invalidated
            {
                get => _invalidated;
                set => _invalidated = value;
            }

            public void Complete(object? result) => _completion.TrySetResult(result);

            public void Fail(Exception exception)
            {
                if (_completion.TrySetException(exception))
                {
                    _ = _completion.Task.Exception; // observed: no UnobservedTaskException when nobody joined
                }
            }
        }

        #endregion
    }
}
