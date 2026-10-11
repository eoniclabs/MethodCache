using System.Diagnostics;
using System.Globalization;
using MethodCache.Core.Configuration.Surfaces.Fluent;
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

            return new ValueTask<CacheLookupResult<T>>(cacheManager.TryGetFast<T>(VersionedKey(key, version), out var value)
                ? CacheLookupResult<T>.Hit(value!)
                : CacheLookupResult<T>.Miss);
        }

        /// <summary>
        /// Gets multiple cache values or creates them using the provided batch factory.
        /// </summary>
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

        /// <summary>The key a fluent entry is stored under: the caller's key, plus its version when it has one.</summary>
        private static string VersionedKey(string key, int? version)
            => version.HasValue ? string.Concat(key, "::v", version.Value.ToString(CultureInfo.InvariantCulture)) : key;

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
