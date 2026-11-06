using System.Runtime.ExceptionServices;
using MethodCache.Core.Telemetry;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;

namespace MethodCache
{
    /// <summary>
    /// Fluent helpers for consuming <see cref="ICacheManager"/> outside of source-generated proxies.
    /// </summary>
    public static class CacheManagerExtensions
    {
        private const string FluentMethodName = CacheKeyLookup.MethodName;
        private const string BulkMethodName = "MethodCache.Fluent.Bulk";

        // Distributed lookups a bulk read runs at once, so a large batch doesn't exhaust the cache's connections
        private const int MaxConcurrentDistributedReads = 8;

        /// <summary>
        /// Gets the value cached under <paramref name="key"/>, or runs <paramref name="factory"/> and caches its result
        /// with the options set in <paramref name="configure"/>.
        /// </summary>
        /// <remarks>
        /// Concurrent callers for the same key share one factory run. <paramref name="cancellationToken"/> cancels
        /// this caller's wait; when it cancels a factory run other callers joined, they run the factory again.
        /// <paramref name="configure"/> builds new options on every call; on a hot path, build them once and pass the
        /// <see cref="CacheEntryOptions"/> instead.
        /// </remarks>
        public static ValueTask<T> GetOrCreateAsync<T>(this ICacheManager cacheManager, string key,
            Func<CancellationToken, ValueTask<T>> factory, Action<CacheEntryOptions.Builder>? configure = null,
            CancellationToken cancellationToken = default)
            => GetOrCreateAsync(cacheManager, key, factory, BuildOptions(configure), cancellationToken);

        /// <summary>
        /// Gets the value cached under <paramref name="key"/>, or runs <paramref name="factory"/> and caches its result
        /// with <paramref name="options"/>, which can be built once and reused for every call.
        /// </summary>
        /// <remarks>
        /// Concurrent callers for the same key share one factory run. <paramref name="cancellationToken"/> cancels
        /// this caller's wait; when it cancels a factory run other callers joined, they run the factory again.
        /// </remarks>
        public static ValueTask<T> GetOrCreateAsync<T>(this ICacheManager cacheManager, string key,
            Func<CancellationToken, ValueTask<T>> factory, CacheEntryOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(options);
            ValidateKey(key);
            return cacheManager.GetOrCreateAsync(key, factory, static (load, token) => load(token), options, cancellationToken);
        }

        /// <summary>
        /// Stores <paramref name="value"/> under <paramref name="key"/> with the options set in
        /// <paramref name="configure"/>, in memory and in the distributed cache, replacing the value every instance holds.
        /// </summary>
        /// <remarks>
        /// Use it when you already have the new value, such as right after you saved it, so the next read doesn't load it
        /// again. Other instances drop their copy and read the new one from the distributed cache. A load already running
        /// for the key doesn't overwrite it. A value the options reject, such as one a <c>CacheWhen</c> predicate turns
        /// down, isn't stored, and the key's old value is removed instead. With <c>WithEnabled(false)</c> nothing happens.
        /// </remarks>
        public static ValueTask SetAsync<T>(this ICacheManager cacheManager, string key, T value,
            Action<CacheEntryOptions.Builder>? configure = null, CancellationToken cancellationToken = default)
            => SetAsync(cacheManager, key, value, BuildOptions(configure), cancellationToken);

        /// <summary>
        /// Stores <paramref name="value"/> under <paramref name="key"/> with <paramref name="options"/>, in memory and in
        /// the distributed cache, replacing the value every instance holds.
        /// </summary>
        /// <remarks>
        /// Use it when you already have the new value, such as right after you saved it, so the next read doesn't load it
        /// again. Other instances drop their copy and read the new one from the distributed cache. A load already running
        /// for the key doesn't overwrite it. A value the options reject, such as one a <c>CacheWhen</c> predicate turns
        /// down, isn't stored, and the key's old value is removed instead. With <c>WithEnabled(false)</c> nothing happens.
        /// A manager of your own gets the key invalidated and then the value through its <c>GetOrCreateAsync</c>.
        /// </remarks>
        public static ValueTask SetAsync<T>(this ICacheManager cacheManager, string key, T value, CacheEntryOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            ArgumentNullException.ThrowIfNull(options);
            ValidateKey(key);
            return FindMemoryManager(cacheManager) is { } memory
                ? memory.SetUserAsync(key, value, options, cancellationToken)
                : SetCustomAsync(cacheManager, key, value, options, cancellationToken);
        }

        private static async ValueTask SetCustomAsync<T>(ICacheManager manager, string key, T value, CacheEntryOptions options,
            CancellationToken cancellationToken)
        {
            if (options.Enabled == false) return;
            // A setting for another type fails before the old value is removed
            CachePredicateCheck.EnsureMatches(options.CacheWhen, typeof(T), key);
            CachePredicateCheck.EnsureMatches(options.DurationFor, typeof(T), key);
            CachePredicateCheck.EnsureMatches(options.TagsFor, typeof(T), key);
            await manager.InvalidateKeysAsync([key], options.Version, cancellationToken).ConfigureAwait(false);
            await manager.GetOrCreateAsync(key, value, static (item, _) => new ValueTask<T>(item), options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Invalidates one key cached without a version, such as a key from <see cref="IMethodCacheKeys.For{TService}"/>,
        /// which already carries the method's version.
        /// </summary>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="key">The entry's key.</param>
        /// <param name="cancellationToken">Cancels this caller's wait; the invalidation itself is not undone.</param>
        public static ValueTask<InvalidationResult> InvalidateKeyAsync(this ICacheManager cacheManager, string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            return cacheManager.InvalidateKeysAsync([key], null, cancellationToken);
        }

        /// <summary>Invalidates one key cached with <paramref name="version"/> (<see cref="CacheEntryOptions.Version"/>).</summary>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="key">The entry's key.</param>
        /// <param name="version">The version the entry was cached with.</param>
        /// <param name="cancellationToken">Cancels this caller's wait; the invalidation itself is not undone.</param>
        public static ValueTask<InvalidationResult> InvalidateKeyAsync(this ICacheManager cacheManager, string key,
            int version, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            return cacheManager.InvalidateKeysAsync([key], version, cancellationToken);
        }

        /// <summary>Invalidates keys cached without a version, such as keys from <see cref="IMethodCacheKeys.For{TService}"/>.</summary>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="keys">The entries' keys.</param>
        /// <param name="cancellationToken">Cancels this caller's wait; the invalidation itself is not undone.</param>
        public static ValueTask<InvalidationResult> InvalidateKeysAsync(this ICacheManager cacheManager, IEnumerable<string> keys,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            return cacheManager.InvalidateKeysAsync(keys, null, cancellationToken);
        }

        /// <summary>Invalidates keys cached with <paramref name="version"/> (<see cref="CacheEntryOptions.Version"/>).</summary>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="keys">The entries' keys.</param>
        /// <param name="version">The version the entries were cached with.</param>
        /// <param name="cancellationToken">Cancels this caller's wait; the invalidation itself is not undone.</param>
        public static ValueTask<InvalidationResult> InvalidateKeysAsync(this ICacheManager cacheManager, IEnumerable<string> keys,
            int version, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            return cacheManager.InvalidateKeysAsync(keys, version, cancellationToken);
        }

        /// <summary>
        /// Reads a key cached without a version, such as a key from <see cref="IMethodCacheKeys.For{TService}"/>, reporting
        /// presence separately, including a cached null, zero and false. Never runs a factory.
        /// </summary>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="key">The entry's key.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        public static ValueTask<CacheLookupResult<T>> TryGetAsync<T>(this ICacheManager cacheManager, string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            return cacheManager.TryGetAsync<T>(key, null, cancellationToken);
        }

        /// <summary>
        /// Reads a key cached with <paramref name="version"/> (<see cref="CacheEntryOptions.Version"/>), reporting presence
        /// separately, including a cached null, zero and false. Never runs a factory.
        /// </summary>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="key">The entry's key.</param>
        /// <param name="version">The version the entry was cached with.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        public static ValueTask<CacheLookupResult<T>> TryGetAsync<T>(this ICacheManager cacheManager, string key,
            int version, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            return cacheManager.TryGetAsync<T>(key, version, cancellationToken);
        }

        /// <summary>Invalidates one application tag.</summary>
        public static ValueTask<InvalidationResult> InvalidateTagAsync(this ICacheManager cacheManager, string tag,
            CancellationToken cancellationToken = default)
            => cacheManager.InvalidateTagsAsync([tag], cancellationToken);

        /// <summary>
        /// Gets the values cached under <paramref name="keys"/>, and runs <paramref name="factory"/> once for the keys
        /// that are not cached, caching its results with the options set in <paramref name="configure"/>.
        /// </summary>
        /// <remarks>
        /// <para>Each key behaves as <c>GetOrCreateAsync</c> would for it. Keys missing from memory are looked up in the
        /// distributed cache first, so the factory receives only the keys neither holds. A concurrent writer may still
        /// supply the final value for a key the factory produced; the returned dictionary uses the value the cache
        /// keeps.</para>
        /// <para>A key the factory leaves out is not cached. It gets its fail-safe value when it has one, as when the
        /// factory throws, and is otherwise left out of the returned dictionary. To cache that a key has no value, return
        /// it with <see langword="null"/>.</para>
        /// <para>When the factory throws, every key it was asked for gets its fail-safe value, as with
        /// <c>GetOrCreateAsync</c>; when a key has none, the factory's exception is thrown.
        /// <paramref name="cancellationToken"/> cancels this caller's wait: a factory that does not observe it finishes in
        /// the background, and its values are cached unless they were invalidated while it ran.</para>
        /// <para><paramref name="configure"/> builds new options on every call; on a hot path, build them once and pass
        /// the <see cref="CacheEntryOptions"/> instead.</para>
        /// </remarks>
        public static ValueTask<IReadOnlyDictionary<string, T>> GetOrCreateManyAsync<T>(
            this ICacheManager cacheManager,
            IEnumerable<string> keys,
            Func<IReadOnlyList<string>, CancellationToken, ValueTask<IReadOnlyDictionary<string, T>>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            CancellationToken cancellationToken = default)
            => GetOrCreateManyAsync(cacheManager, keys, factory, BuildOptions(configure), cancellationToken);

        /// <summary>
        /// Gets the values cached under <paramref name="keys"/>, and runs <paramref name="factory"/> once for the keys
        /// that are not cached, caching its results with <paramref name="options"/>, which can be built once and
        /// reused for every call.
        /// </summary>
        /// <remarks>
        /// <para>Each key behaves as <c>GetOrCreateAsync</c> would for it. Keys missing from memory are looked up in the
        /// distributed cache first, so the factory receives only the keys neither holds. A concurrent writer may still
        /// supply the final value for a key the factory produced; the returned dictionary uses the value the cache
        /// keeps.</para>
        /// <para>A key the factory leaves out is not cached. It gets its fail-safe value when it has one, as when the
        /// factory throws, and is otherwise left out of the returned dictionary. To cache that a key has no value, return
        /// it with <see langword="null"/>.</para>
        /// <para>When the factory throws, every key it was asked for gets its fail-safe value, as with
        /// <c>GetOrCreateAsync</c>; when a key has none, the factory's exception is thrown.
        /// <paramref name="cancellationToken"/> cancels this caller's wait: a factory that does not observe it finishes in
        /// the background, and its values are cached unless they were invalidated while it ran.</para>
        /// </remarks>
        public static async ValueTask<IReadOnlyDictionary<string, T>> GetOrCreateManyAsync<T>(
            this ICacheManager cacheManager,
            IEnumerable<string> keys,
            Func<IReadOnlyList<string>, CancellationToken, ValueTask<IReadOnlyDictionary<string, T>>> factory,
            CacheEntryOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            ArgumentNullException.ThrowIfNull(keys);
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(options);

            cancellationToken.ThrowIfCancellationRequested();

            var keyList = keys.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var key in keyList) ValidateKey(key);
            var memory = FindMemoryManager(cacheManager);
            if (memory == null)
                return await GetOrCreateManyCustomAsync(cacheManager, keyList, factory, options, cancellationToken).ConfigureAwait(false);
            var telemetry = memory.Telemetry;
            var outcome = RequestResult.Hit;
            try
            {
                var bypass = options.Enabled == false;
                var results = new Dictionary<string, T>(keyList.Length, StringComparer.Ordinal);
                var missingKeys = new List<string>();
                foreach (var key in keyList)
                {
                    // A real lookup: the fast path also reports entries due for an early refresh as missing,
                    // and with EnableFastPath off every entry, which would send cached keys to the factory
                    var cached = bypass ? default : memory.TryGetFreshForLookup<T>(MethodCacheKeys.VersionedKey(key, options.Version));
                    if (cached.Found) results[key] = cached.Value!;
                    else missingKeys.Add(key);
                }

                if (missingKeys.Count > 0 && !bypass)
                {
                    memory.EnsureWithinMaxExpiration(options, nameof(options));
                    missingKeys = await TakeDistributedHitsAsync(memory, missingKeys, results, options, cancellationToken).ConfigureAwait(false);
                }

                if (missingKeys.Count > 0)
                {
                    // Like a single load, the batch goes on when this caller stops waiting
                    var load = LoadBatchAsync(cacheManager, memory, missingKeys, factory, options, cancellationToken);
                    Observe(load);
                    var (loaded, stale) = await load.WaitAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var pair in loaded) results[pair.Key] = pair.Value;
                    outcome = bypass ? RequestResult.Bypass : stale ? RequestResult.Stale : RequestResult.Miss;
                }

                return results;
            }
            catch { outcome = RequestResult.Error; throw; }
            finally { telemetry?.Request("method", BulkMethodName, outcome); }
        }

        /// <summary>
        /// Gets the values cached for <paramref name="ids"/>, under the key <paramref name="keyFor"/> gives each id, and
        /// runs <paramref name="factory"/> once for the ids that are not cached, caching its results with the options set
        /// in <paramref name="configure"/>.
        /// </summary>
        /// <remarks>
        /// <para>Works as the overload with string keys: the factory gets the ids neither cache holds and returns a
        /// dictionary by id. An id the factory leaves out is not cached; it gets its fail-safe value when it has one, and
        /// is otherwise left out of the returned dictionary.</para>
        /// <para><paramref name="configure"/> builds new options on every call; on a hot path, build them once and pass
        /// the <see cref="CacheEntryOptions"/> instead.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">Two different ids give the same key.</exception>
        public static ValueTask<IReadOnlyDictionary<TId, T>> GetOrCreateManyAsync<TId, T>(
            this ICacheManager cacheManager,
            IEnumerable<TId> ids,
            Func<TId, string> keyFor,
            Func<IReadOnlyList<TId>, CancellationToken, ValueTask<IReadOnlyDictionary<TId, T>>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            CancellationToken cancellationToken = default)
            where TId : notnull
            => GetOrCreateManyAsync(cacheManager, ids, keyFor, factory, BuildOptions(configure), cancellationToken);

        /// <summary>
        /// Gets the values cached for <paramref name="ids"/>, under the key <paramref name="keyFor"/> gives each id, and
        /// runs <paramref name="factory"/> once for the ids that are not cached, caching its results with
        /// <paramref name="options"/>, which can be built once and reused for every call.
        /// </summary>
        /// <remarks>
        /// Works as the overload with string keys: the factory gets the ids neither cache holds and returns a dictionary
        /// by id. An id the factory leaves out is not cached; it gets its fail-safe value when it has one, and is
        /// otherwise left out of the returned dictionary.
        /// </remarks>
        /// <exception cref="ArgumentException">Two different ids give the same key.</exception>
        public static async ValueTask<IReadOnlyDictionary<TId, T>> GetOrCreateManyAsync<TId, T>(
            this ICacheManager cacheManager,
            IEnumerable<TId> ids,
            Func<TId, string> keyFor,
            Func<IReadOnlyList<TId>, CancellationToken, ValueTask<IReadOnlyDictionary<TId, T>>> factory,
            CacheEntryOptions options,
            CancellationToken cancellationToken = default)
            where TId : notnull
        {
            ArgumentNullException.ThrowIfNull(cacheManager);
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(keyFor);
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(options);

            var idByKey = new Dictionary<string, TId>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var key = keyFor(id);
                ValidateKey(key);
                if (!idByKey.TryAdd(key, id) && !EqualityComparer<TId>.Default.Equals(idByKey[key], id))
                {
                    throw new ArgumentException(
                        $"The ids '{idByKey[key]}' and '{id}' both give the key '{key}', so one would get the other's value. " +
                        "Give each id its own key.", nameof(keyFor));
                }
            }

            var values = await GetOrCreateManyAsync<T>(cacheManager, idByKey.Keys, async (missingKeys, token) =>
            {
                var missingIds = new TId[missingKeys.Count];
                for (var i = 0; i < missingIds.Length; i++) missingIds[i] = idByKey[missingKeys[i]];
                var loaded = EnsureAnswer(await factory(missingIds, token).ConfigureAwait(false));
                var byKey = new Dictionary<string, T>(loaded.Count, StringComparer.Ordinal);
                for (var i = 0; i < missingIds.Length; i++)
                    if (loaded.TryGetValue(missingIds[i], out var value)) byKey[missingKeys[i]] = value;
                return byKey;
            }, options, cancellationToken).ConfigureAwait(false);

            var results = new Dictionary<TId, T>(values.Count);
            foreach (var (key, value) in values) results[idByKey[key]] = value;
            return results;
        }

        /// <summary>
        /// Runs the batch factory for keys missing from the built-in manager and publishes its values. Reports whether
        /// the factory failed and the keys got their fail-safe values instead.
        /// </summary>
        private static async Task<(Dictionary<string, T> Values, bool Stale)> LoadBatchAsync<T>(ICacheManager cacheManager,
            InMemoryCacheManager memory, List<string> keys,
            Func<IReadOnlyList<string>, CancellationToken, ValueTask<IReadOnlyDictionary<string, T>>> factory,
            CacheEntryOptions options, CancellationToken cancellationToken)
        {
            var bypass = options.Enabled == false;
            using var batch = bypass ? null : memory.BeginBulkLoad(keys.Select(key => MethodCacheKeys.VersionedKey(key, options.Version)),
                CreateRuntimePolicy(FluentMethodName, options));
            IReadOnlyDictionary<string, T>? produced = null;
            Exception? failure = null;
            using (var origin = memory.Telemetry?.BeginLoad("method", BulkMethodName, tags: options.Tags))
            {
                try { produced = EnsureAnswer(await factory(keys, cancellationToken).ConfigureAwait(false)); }
                catch (Exception error)
                {
                    origin?.Fail(error);
                    // As for a single load: no fallback for a caller that cancelled, nor without caching
                    if (bypass || (error is OperationCanceledException && cancellationToken.IsCancellationRequested)) throw;
                    failure = error;
                }
            }

            var values = new Dictionary<string, T>(keys.Count, StringComparer.Ordinal);
            if (bypass)
            {
                // Without caching there is no fail-safe value, so a key the factory left out is left out
                foreach (var key in keys)
                    if (produced!.TryGetValue(key, out var value)) values[key] = value;
                return (values, false);
            }

            // Each key takes the single-key load path, so a distributed hit or a concurrent writer can still win over the
            // batch's value, an invalidation during the batch keeps its value out of the cache, and a failed batch, or a
            // key the batch left out, gets its fail-safe value. Not with the caller's token: publication outlives a caller
            // that stopped waiting.
            var unserved = false;
            foreach (var key in keys)
            {
                Func<Task<T>> load;
                if (failure != null)
                {
                    load = () => Task.FromException<T>(failure);
                }
                else if (produced!.TryGetValue(key, out var value))
                {
                    load = () => Task.FromResult(value);
                }
                else
                {
                    var leftOut = new LeftOutOfBatchException(key);
                    load = () => Task.FromException<T>(leftOut);
                }

                try
                {
                    values[key] = await batch!.GetOrCreateAsync(cacheManager, MethodCacheKeys.VersionedKey(key, options.Version), load, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (LeftOutOfBatchException)
                {
                    // Left out by the batch and without a fail-safe value: not in the result
                }
                catch (Exception) when (failure != null)
                {
                    // No fail-safe value for this key; the others still get theirs
                    unserved = true;
                }
            }

            if (unserved) ExceptionDispatchInfo.Throw(failure!);
            return (values, failure != null);
        }

        private static async ValueTask<IReadOnlyDictionary<string, T>> GetOrCreateManyCustomAsync<T>(ICacheManager manager,
            string[] keys, Func<IReadOnlyList<string>, CancellationToken, ValueTask<IReadOnlyDictionary<string, T>>> factory,
            CacheEntryOptions options, CancellationToken token)
        {
            var results = new Dictionary<string, T>(StringComparer.Ordinal);
            if (options.Enabled == false)
            {
                var bypass = await RunFactoryAsync(factory, keys, token).WaitAsync(token).ConfigureAwait(false);
                foreach (var key in keys)
                    if (bypass.TryGetValue(key, out var value)) results[key] = value;
                return results;
            }
            var hits = new CacheLookupResult<T>[keys.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, keys.Length),
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDistributedReads, CancellationToken = token }, async (index, ct) =>
                {
                    var lookup = manager.TryGetAsync<T>(keys[index], options.Version, ct);
                    hits[index] = lookup.IsCompletedSuccessfully ? lookup.Result : await lookup.AsTask().WaitAsync(ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
            var missing = new List<string>();
            for (var i = 0; i < keys.Length; i++)
                if (hits[i].Found && options.CanCache(hits[i].Value)) results[keys[i]] = hits[i].Value!;
                else missing.Add(keys[i]);
            if (missing.Count == 0) return results;

            // Every missing key loads through the manager, so its own single-flight, fail-safe and invalidation rules
            // apply per key; the loads it starts wait for one batch instead of each running the factory.
            var produced = new TaskCompletionSource<IReadOnlyDictionary<string, T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Observe(produced.Task);
            var entered = new bool[missing.Count];
            var arrivals = new TaskCompletionSource[missing.Count];
            var pending = new Task<T>[missing.Count];
            Task<IReadOnlyDictionary<string, T>>? batch = null;
            try
            {
                for (var i = 0; i < missing.Count; i++)
                {
                    var index = i;
                    arrivals[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending[i] = manager.GetOrCreateAsync(missing[i], async _ =>
                    {
                        entered[index] = true;
                        arrivals[index].TrySetResult();
                        // Like a single load's factory, the batch alone decides whether the caller's token stops it
                        var values = await produced.Task.ConfigureAwait(false);
                        // A key the batch left out fails its load, so the manager can give it its fail-safe value
                        return values.TryGetValue(missing[index], out var value) ? value : throw new LeftOutOfBatchException(missing[index]);
                    }, options, token).AsTask();
                    Observe(pending[i]);
                    _ = pending[i].ContinueWith(static (_, state) => ((TaskCompletionSource)state!).TrySetResult(), arrivals[i],
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                await Task.WhenAll(arrivals.Select(static arrival => arrival.Task)).WaitAsync(token).ConfigureAwait(false);
                foreach (var load in pending.Where(static load => load.IsFaulted || load.IsCanceled)) await load.ConfigureAwait(false);
                var required = missing.Where((_, index) => entered[index]).ToArray();
                batch = required.Length == 0
                    ? Task.FromResult<IReadOnlyDictionary<string, T>>(new Dictionary<string, T>())
                    : RunFactoryAsync(factory, required, token);
                // The loads get the batch's outcome also after this caller stopped waiting
                _ = ForwardAsync(batch, produced);

                Exception? failure = null;
                try { await batch.WaitAsync(token).ConfigureAwait(false); }
                catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested))
                {
                    // The manager may still give each key its fail-safe value, as it would for GetOrCreateAsync
                    failure = error;
                }

                try { await Task.WhenAll(pending).WaitAsync(token).ConfigureAwait(false); }
                catch (Exception) when (failure == null && !token.IsCancellationRequested &&
                    pending.All(static load => load.IsCompletedSuccessfully || load.Exception?.InnerException is LeftOutOfBatchException))
                {
                    // Only keys the batch left out failed, and they had no fail-safe value: they are not in the result
                }
                catch (Exception) when (failure != null && !token.IsCancellationRequested)
                {
                    ExceptionDispatchInfo.Throw(failure);
                    throw;
                }

                for (var i = 0; i < missing.Count; i++)
                    if (pending[i].IsCompletedSuccessfully) results[missing[i]] = pending[i].Result;
                return results;
            }
            catch (Exception error) when (batch == null)
            {
                // The batch never ran: release the loads waiting for it
                if (error is OperationCanceledException && token.IsCancellationRequested) produced.TrySetCanceled(token);
                else produced.TrySetException(error);
                throw;
            }
        }

        private static async Task<IReadOnlyDictionary<string, T>> RunFactoryAsync<T>(
            Func<IReadOnlyList<string>, CancellationToken, ValueTask<IReadOnlyDictionary<string, T>>> factory,
            IReadOnlyList<string> keys, CancellationToken token)
            => EnsureAnswer(await factory(keys, token).ConfigureAwait(false));

        /// <summary>Rejects a <see langword="null"/> answer, which fails like a factory that throws.</summary>
        private static IReadOnlyDictionary<TKey, T> EnsureAnswer<TKey, T>(IReadOnlyDictionary<TKey, T>? values) where TKey : notnull
            => values ?? throw new InvalidOperationException(
                "The bulk cache factory returned null. Return a dictionary with the keys that have a value; an empty one when none has.");

        /// <summary>
        /// Fails the load of a key the bulk factory left out, so it gets its fail-safe value or stays out of the result,
        /// and nothing is cached for it.
        /// </summary>
        private sealed class LeftOutOfBatchException(string key)
            : InvalidOperationException($"The bulk cache factory returned no value for key '{key}', so nothing was cached for it.");

        private static async Task ForwardAsync<T>(Task<T> source, TaskCompletionSource<T> target)
        {
            try { target.TrySetResult(await source.ConfigureAwait(false)); }
            catch (OperationCanceledException cancelled) { target.TrySetCanceled(cancelled.CancellationToken); }
            catch (Exception error) { target.TrySetException(error); }
        }

        private static void Observe(Task task)
            => _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        /// <summary>
        /// Reads the distributed cache for the keys missing from memory, a few at a time, and moves the hits into
        /// <paramref name="results"/>. Returns the keys still missing.
        /// </summary>
        private static async Task<List<string>> TakeDistributedHitsAsync<T>(
            InMemoryCacheManager manager,
            List<string> missingKeys,
            Dictionary<string, T> results,
            CacheEntryOptions options,
            CancellationToken cancellationToken)
        {
            var policy = CreateRuntimePolicy(FluentMethodName, options);
            var hits = new (bool Found, T? Value)[missingKeys.Count];
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDistributedReads, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(Enumerable.Range(0, missingKeys.Count), parallel, async (index, ct) =>
                hits[index] = await manager.TryGetDistributedAsync<T>(MethodCacheKeys.VersionedKey(missingKeys[index], options.Version), FluentMethodName, policy, ct).ConfigureAwait(false))
                .ConfigureAwait(false);

            var stillMissing = new List<string>(missingKeys.Count);
            for (var i = 0; i < missingKeys.Count; i++)
            {
                if (hits[i].Found) results[missingKeys[i]] = hits[i].Value!;
                else stillMissing.Add(missingKeys[i]);
            }

            return stillMissing;
        }

        /// <summary>
        /// The built-in in-memory manager, or <see langword="null"/> for another manager.
        /// </summary>
        private static InMemoryCacheManager? FindMemoryManager(ICacheManager cacheManager)
        {
            while (cacheManager is ICacheManagerDecorator decorator) cacheManager = decorator.InnerManager;
            return cacheManager as InMemoryCacheManager;
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "Cache key cannot be null or whitespace. " +
                    "Provide a unique string identifier for this cache entry. " +
                    "Keys should be descriptive and unique (e.g., 'user:123', 'product:456:details'). " +
                    "Consider using a key generator or manual key construction. " +
                    "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/features/manual-caching.md#keys",
                    nameof(key));
            }
        }

        private static CacheEntryOptions BuildOptions(Action<CacheEntryOptions.Builder>? configure)
        {
            if (configure == null) return CacheEntryOptions.Default;
            var builder = new CacheEntryOptions.Builder();
            configure(builder);
            return builder.Build();
        }

        private static CacheRuntimePolicy CreateRuntimePolicy(string methodId, CacheEntryOptions options)
        {
            var (policy, fields) = CachePolicyBuilderFactory.FromOptions(options).BuildPolicy();
            return CacheRuntimePolicy.FromPolicy(methodId, policy, fields);
        }

    }
}
