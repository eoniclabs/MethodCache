using System.Globalization;
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

        /// <summary>Invalidates one logical key, optionally in a versioned key family.</summary>
        public static ValueTask<InvalidationResult> InvalidateKeyAsync(this ICacheManager cacheManager, string key,
            int? version = null, CancellationToken cancellationToken = default)
            => cacheManager.InvalidateKeysAsync([key], version, cancellationToken);

        /// <summary>Invalidates one application tag.</summary>
        public static ValueTask<InvalidationResult> InvalidateTagAsync(this ICacheManager cacheManager, string tag,
            CancellationToken cancellationToken = default)
            => cacheManager.InvalidateTagsAsync([tag], cancellationToken);

        /// <summary>
        /// Gets the values cached under <paramref name="keys"/>, and runs <paramref name="factory"/> once for the keys
        /// that are not cached, caching its results with the options set in <paramref name="configure"/>.
        /// </summary>
        /// <remarks>
        /// Keys missing from memory are looked up in the distributed cache first, so the factory receives only the
        /// keys neither holds, and must return a value for each of them. A concurrent writer may still supply the final
        /// value for a key the factory produced; the returned dictionary uses the value the cache keeps.
        /// <paramref name="configure"/> builds new options on every call; on a hot path, build them once and pass the
        /// <see cref="CacheEntryOptions"/> instead.
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
        /// Keys missing from memory are looked up in the distributed cache first, so the factory receives only the
        /// keys neither holds, and must return a value for each of them. A concurrent writer may still supply the final
        /// value for a key the factory produced; the returned dictionary uses the value the cache keeps.
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
            if (FindMemoryManager(cacheManager) == null)
                return await GetOrCreateManyCustomAsync(cacheManager, keyList, factory, options, cancellationToken).ConfigureAwait(false);
            var telemetry = FindMemoryManager(cacheManager)!.Telemetry;
            var outcome = RequestResult.Hit;
            try
            {
                var results = new Dictionary<string, T>(keyList.Length);
                var missingKeys = new List<string>();
                HashSet<string>? skipCacheKeys = options.Enabled == false ? new HashSet<string>(keyList, StringComparer.Ordinal) : null;

                foreach (var key in keyList)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        throw new ArgumentException("Keys must not contain null or whitespace entries.", nameof(keys));
                    }

                    if (options.Enabled != false)
                    {
                        // A real lookup: the fast path also reports entries due for an early refresh as missing,
                        // and with EnableFastPath off every entry, which would send cached keys to the factory
                        var cached = FindMemoryManager(cacheManager) is { } local
                            ? local.TryGetFreshForLookup<T>(VersionedKey(key, options.Version))
                            : await cacheManager.TryGetAsync<T>(key, options.Version, cancellationToken).ConfigureAwait(false);
                        if (cached.Found)
                        {
                            results[key] = cached.Value!;
                            continue;
                        }
                    }
                    else
                    {
                        skipCacheKeys!.Add(key);
                    }

                    missingKeys.Add(key);
                }

                if (missingKeys.Count > 0 && FindMemoryManager(cacheManager) is { } manager)
                {
                    missingKeys = await TakeDistributedHitsAsync(manager, missingKeys, skipCacheKeys, results, options, cancellationToken).ConfigureAwait(false);
                }

                if (missingKeys.Count > 0)
                {
                    var policy = CreateRuntimePolicy(FluentMethodName, options);
                    using var batch = options.Enabled == false ? null : FindMemoryManager(cacheManager)?.BeginBulkLoad(
                        missingKeys.Where(key => skipCacheKeys == null || !skipCacheKeys.Contains(key))
                            .Select(key => VersionedKey(key, options.Version)), policy);
                    IReadOnlyDictionary<string, T> produced;
                    using (var origin = telemetry?.BeginLoad("method", BulkMethodName, tags: options.Tags))
                    {
                        try { produced = await factory(missingKeys, cancellationToken).ConfigureAwait(false); }
                        catch (Exception error) { origin?.Fail(error); throw; }
                    }
                    outcome = options.Enabled == false ? RequestResult.Bypass : RequestResult.Miss;

                    foreach (var key in missingKeys)
                    {
                        if (!produced.TryGetValue(key, out var value))
                        {
                            throw new InvalidOperationException($"Bulk cache factory did not return a value for key '{key}'.");
                        }

                        if (skipCacheKeys != null && skipCacheKeys.Contains(key))
                        {
                            results[key] = value!;
                            continue;
                        }

                        // A distributed hit or a concurrent publisher can win over this batch's candidate value.
                        var cacheKey = VersionedKey(key, options.Version);
                        results[key] = await (batch != null
                            ? batch.GetOrCreateAsync(cacheManager, cacheKey, () => Task.FromResult(value!), cancellationToken)
                            : cacheManager.GetOrCreateFastAsync(cacheKey, FluentMethodName, () => Task.FromResult(value!), policy, cancellationToken))
                            .ConfigureAwait(false);
                    }
                }

                return results;
            }
            catch { outcome = RequestResult.Error; throw; }
            finally { telemetry?.Request("method", BulkMethodName, outcome); }
        }

        private static async ValueTask<IReadOnlyDictionary<string, T>> GetOrCreateManyCustomAsync<T>(ICacheManager manager,
            string[] keys, Func<IReadOnlyList<string>, CancellationToken, ValueTask<IReadOnlyDictionary<string, T>>> factory,
            CacheEntryOptions options, CancellationToken token)
        {
            var results = new Dictionary<string, T>(StringComparer.Ordinal);
            if (options.Enabled == false)
            {
                var bypass = await factory(keys, token).ConfigureAwait(false);
                foreach (var key in keys) results[key] = Candidate(bypass, key);
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

            var produced = new TaskCompletionSource<IReadOnlyDictionary<string, T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Observe(produced.Task);
            var entered = new bool[missing.Count];
            var arrivals = new TaskCompletionSource[missing.Count];
            var pending = new Task<T>[missing.Count];
            try
            {
                for (var i = 0; i < missing.Count; i++)
                {
                    var index = i;
                    arrivals[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending[i] = manager.GetOrCreateAsync(missing[i], async ct =>
                    {
                        entered[index] = true;
                        arrivals[index].TrySetResult();
                        return Candidate(await produced.Task.WaitAsync(ct).ConfigureAwait(false), missing[index]);
                    }, options, token).AsTask();
                    Observe(pending[i]);
                    _ = pending[i].ContinueWith(static (_, state) => ((TaskCompletionSource)state!).TrySetResult(), arrivals[i],
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                await Task.WhenAll(arrivals.Select(static arrival => arrival.Task)).WaitAsync(token).ConfigureAwait(false);
                foreach (var load in pending.Where(static load => load.IsFaulted || load.IsCanceled)) await load.ConfigureAwait(false);
                var required = missing.Where((_, index) => entered[index]).ToArray();
                var candidates = required.Length == 0 ? new Dictionary<string, T>() : await factory(required, token).ConfigureAwait(false);
                produced.TrySetResult(candidates);
                var published = await Task.WhenAll(pending).WaitAsync(token).ConfigureAwait(false);
                for (var i = 0; i < missing.Count; i++) results[missing[i]] = published[i];
                return results;
            }
            catch (Exception error) { produced.TrySetException(error); throw; }
        }

        private static T Candidate<T>(IReadOnlyDictionary<string, T> values, string key)
            => values.TryGetValue(key, out var value) ? value : throw new InvalidOperationException($"Bulk cache factory did not return a value for key '{key}'.");

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
            HashSet<string>? skipCacheKeys,
            Dictionary<string, T> results,
            CacheEntryOptions options,
            CancellationToken cancellationToken)
        {
            var lookups = missingKeys.Distinct(StringComparer.Ordinal)
                .Where(key => skipCacheKeys == null || !skipCacheKeys.Contains(key))
                .ToList();
            if (lookups.Count == 0)
            {
                return missingKeys;
            }

            var policy = CreateRuntimePolicy(FluentMethodName, options);
            var hits = new (bool Found, T? Value)[lookups.Count];
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDistributedReads, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(Enumerable.Range(0, lookups.Count), parallel, async (index, ct) =>
                hits[index] = await manager.TryGetDistributedAsync<T>(VersionedKey(lookups[index], options.Version), FluentMethodName, policy, ct).ConfigureAwait(false))
                .ConfigureAwait(false);

            HashSet<string>? found = null;
            for (var i = 0; i < lookups.Count; i++)
            {
                if (hits[i].Found)
                {
                    results[lookups[i]] = hits[i].Value!;
                    (found ??= new HashSet<string>(StringComparer.Ordinal)).Add(lookups[i]);
                }
            }

            return found == null ? missingKeys : missingKeys.Where(key => !found.Contains(key)).ToList();
        }

        /// <summary>
        /// The built-in in-memory manager, or <see langword="null"/> for another manager.
        /// </summary>
        private static InMemoryCacheManager? FindMemoryManager(ICacheManager cacheManager)
        {
            while (cacheManager is ICacheManagerDecorator decorator) cacheManager = decorator.InnerManager;
            return cacheManager as InMemoryCacheManager;
        }

        /// <summary>Returns the internal key used by the fluent manual cache for a logical key and optional version.</summary>
        /// <param name="key">The logical key supplied to the fluent cache.</param>
        /// <param name="version">The non-negative version supplied to WithVersion, or null for an unversioned entry.</param>
        /// <returns>The key the built-in manager stores the entry under, as <see cref="ICacheAdministrator"/> lists it.</returns>
        /// <remarks>Reserved control characters separate identities. Treat the returned format as opaque.</remarks>
        public static string GetCacheKey(string key, int? version = null)
        {
            ValidateKey(key);
            if (version < 0) throw new ArgumentOutOfRangeException(nameof(version), "Version must be non-negative.");
            return VersionedKey(key, version);
        }

        /// <summary>Separates versioned keys from unversioned user keys, escaping the reserved leading NUL.</summary>
        private static string VersionedKey(string key, int? version)
            => version.HasValue
                ? string.Concat("\0v", version.Value.ToString(CultureInfo.InvariantCulture), ":", key)
                : key[0] == '\0' ? "\0" + key : key;

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
