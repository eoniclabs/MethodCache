using System.Diagnostics;
using System.Globalization;
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
        private const string FluentMethodName = "MethodCache.Fluent";
        private const string BulkMethodName = "MethodCache.Fluent.Bulk";
        private static readonly CacheRuntimePolicy LookupPolicy = CacheRuntimePolicy.Empty(FluentMethodName);
        private static readonly ICacheKeyGenerator LookupKeyGenerator = new ExplicitKeyGenerator();

        // Distributed lookups a bulk read runs at once, so a large batch doesn't exhaust the cache's connections
        private const int MaxConcurrentDistributedReads = 8;

        /// <summary>
        /// Gets a cache value or creates it using the provided factory, using a fluent configuration surface.
        /// </summary>
        /// <remarks>
        /// Concurrent callers for the same key share one factory run. <paramref name="cancellationToken"/> cancels
        /// this caller's wait; when it cancels a factory run other callers joined, they run the factory again.
        /// </remarks>
        public static ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            string key,
            Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            ValidateKey(key);
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var options = BuildOptions(configure);
            var cacheKey = VersionedKey(key, options.Version);

            // A hit needs neither the runtime policy nor a context, unless a predicate or callback asks for one
            var plain = options.Predicate == null && options.Metrics == null && options.OnHitCallbacks.Count == 0;
            if (plain && cacheManager.TryGetFast<T>(cacheKey, out var cached))
            {
                return new ValueTask<T>(cached!);
            }

            return GetOrCreateSlowAsync(cacheManager, key, cacheKey, factory, options, services, cancellationToken);
        }

        private static async ValueTask<T> GetOrCreateSlowAsync<T>(
            ICacheManager cacheManager,
            string key,
            string cacheKey,
            Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions options,
            IServiceProvider? services,
            CancellationToken cancellationToken)
        {
            var context = new CacheContext(key, services);
            var stopwatch = options.Metrics != null ? Stopwatch.StartNew() : null;

            if (options.Predicate != null && !options.Predicate(context))
            {
                var bypassResult = await factory(context, cancellationToken).ConfigureAwait(false);
                options.Metrics?.RecordMiss(context.Key);
                return bypassResult;
            }

            var factoryExecuted = false;
            async Task<T> WrappedFactory()
            {
                factoryExecuted = true;
                InvokeCallbacks(options.OnMissCallbacks, context);

                try
                {
                    var result = await factory(context, cancellationToken).ConfigureAwait(false);
                    options.Metrics?.RecordMiss(context.Key);
                    return result;
                }
                catch (Exception ex)
                {
                    options.Metrics?.RecordError(context.Key, ex);
                    throw;
                }
            }

            var policy = CreateRuntimePolicy(FluentMethodName, options);
            var value = await cacheManager.GetOrCreateFastAsync(cacheKey, FluentMethodName, WrappedFactory, policy, cancellationToken).ConfigureAwait(false);

            if (!factoryExecuted)
            {
                InvokeCallbacks(options.OnHitCallbacks, context);
                options.Metrics?.RecordHit(context.Key, stopwatch?.Elapsed, value);
            }

            return value;
        }

        /// <summary>
        /// Attempts to read a cached value without executing a factory.
        /// </summary>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="key">The key the value was stored under.</param>
        /// <param name="version">The version the value was stored with (<c>WithVersion</c>), if any.</param>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        public static ValueTask<CacheLookupResult<T>> TryGetAsync<T>(
            this ICacheManager cacheManager,
            string key,
            int? version = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            ValidateKey(key);

            cancellationToken.ThrowIfCancellationRequested();

            return cacheManager.LookupAsync<T>(VersionedKey(key, version), Array.Empty<object>(), LookupPolicy, LookupKeyGenerator);
        }

        private sealed class ExplicitKeyGenerator : ICacheKeyGenerator
        {
            public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy) => methodName;
        }

        /// <summary>
        /// Gets multiple cache values or creates them using the provided batch factory.
        /// </summary>
        /// <remarks>
        /// Keys missing from memory are looked up in the distributed cache first, so the factory receives only the
        /// keys neither holds. A concurrent writer may still supply the final value for a key the factory produced;
        /// the returned dictionary uses the value the cache keeps.
        /// </remarks>
        public static async ValueTask<IDictionary<string, T>> GetOrCreateManyAsync<T>(
            this ICacheManager cacheManager,
            IEnumerable<string> keys,
            Func<IReadOnlyList<string>, CacheContext, CancellationToken, ValueTask<IDictionary<string, T>>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var options = BuildOptions(configure);
            var keyList = keys as IList<string> ?? keys.ToList();
            var results = new Dictionary<string, T>(keyList.Count);
            var missingKeys = new List<string>();
            HashSet<string>? skipCacheKeys = options.Predicate != null ? new HashSet<string>(StringComparer.Ordinal) : null;

            foreach (var key in keyList)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("Keys must not contain null or whitespace entries.", nameof(keys));
                }

                var singleContext = new CacheContext(key, services);
                if (options.Predicate == null || options.Predicate(singleContext))
                {
                    if (cacheManager.TryGetFast<T>(VersionedKey(key, options.Version), out var cached))
                    {
                        results[key] = cached!;
                        InvokeCallbacks(options.OnHitCallbacks, singleContext);
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
                missingKeys = await TakeDistributedHitsAsync(manager, missingKeys, skipCacheKeys, results, options, services, cancellationToken).ConfigureAwait(false);
            }

            if (missingKeys.Count > 0)
            {
                var bulkContext = new CacheContext(BulkMethodName, services);
                var produced = await factory(missingKeys, bulkContext, cancellationToken).ConfigureAwait(false);
                var policy = CreateRuntimePolicy(FluentMethodName, options);

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
                    results[key] = await cacheManager.GetOrCreateFastAsync(
                        VersionedKey(key, options.Version),
                        FluentMethodName,
                        () => Task.FromResult(value!),
                        policy,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return results;
        }

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
            IServiceProvider? services,
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
                    InvokeCallbacks(options.OnHitCallbacks, new CacheContext(lookups[i], services));
                    (found ??= new HashSet<string>(StringComparer.Ordinal)).Add(lookups[i]);
                }
            }

            return found == null ? missingKeys : missingKeys.Where(key => !found.Contains(key)).ToList();
        }

        /// <summary>
        /// The in-memory manager behind <paramref name="cacheManager"/>, looking through decorators such as the
        /// telemetry one, or <see langword="null"/> when there is none.
        /// </summary>
        private static InMemoryCacheManager? FindMemoryManager(ICacheManager cacheManager)
        {
            while (cacheManager is ICacheManagerDecorator decorator)
            {
                cacheManager = decorator.InnerManager;
            }

            return cacheManager as InMemoryCacheManager;
        }

        /// <summary>Returns the internal key used by the fluent manual cache for a logical key and optional version.</summary>
        /// <param name="key">The logical key supplied to the fluent cache.</param>
        /// <param name="version">The non-negative version supplied to WithVersion, or null for an unversioned entry.</param>
        /// <returns>The key to pass to InvalidateByKeysAsync or the memory administrator.</returns>
        /// <remarks>Reserved control characters separate identities. Treat the returned format as opaque; CacheContext.Key retains the logical key.</remarks>
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
            var builder = new CacheEntryOptions.Builder();
            configure?.Invoke(builder);
            return builder.Build();
        }

        private static CacheRuntimePolicy CreateRuntimePolicy(string methodId, CacheEntryOptions options)
        {
            var (policy, fields) = CachePolicyBuilderFactory.FromOptions(options).BuildPolicy();
            return CacheRuntimePolicy.FromPolicy(methodId, policy, fields);
        }

        private static void InvokeCallbacks(IReadOnlyList<Action<CacheContext>> callbacks, CacheContext context)
        {
            foreach (var callback in callbacks)
            {
                callback(context);
            }
        }
    }
}
