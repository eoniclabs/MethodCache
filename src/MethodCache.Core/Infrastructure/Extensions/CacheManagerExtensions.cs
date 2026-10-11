using System.Diagnostics;
using System.Runtime.CompilerServices;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Runtime.Options;

namespace MethodCache.Core.Infrastructure.Extensions
{
    /// <summary>
    /// Fluent helpers for consuming <see cref="ICacheManager"/> outside of source-generated proxies.
    /// </summary>
    public static class CacheManagerExtensions
    {
        private const string FluentMethodName = "MethodCache.Fluent";
        private const string BulkMethodName = "MethodCache.Fluent.Bulk";
        private static readonly object[] EmptyArgs = Array.Empty<object>();

        /// <summary>
        /// Gets a cache value or creates it using the provided factory, using a fluent configuration surface.
        /// </summary>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            string key,
            Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "Cache key cannot be null or whitespace. " +
                    "Provide a unique string identifier for this cache entry. " +
                    "Keys should be descriptive and unique (e.g., 'user:123', 'product:456:details'). " +
                    "Consider using a key generator or manual key construction. " +
                    "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/CONFIGURATION_GUIDE.md#cache-keys",
                    nameof(key));
            }
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var options = BuildOptions(configure);
            var policy = CreateRuntimePolicy(FluentMethodName, options);
            var runtimeOptions = CacheRuntimeOptions.From(options);
            var context = new CacheContext(key, services);
            var factoryExecuted = false;
            var stopwatch = runtimeOptions.Metrics != null ? Stopwatch.StartNew() : null;

            if (options.Predicate != null && !options.Predicate(context))
            {
                var bypassResult = await factory(context, cancellationToken).ConfigureAwait(false);
                runtimeOptions.Metrics?.RecordMiss(context.Key);
                return bypassResult;
            }

            async Task<T> WrappedFactory()
            {
                factoryExecuted = true;
                InvokeMissCallbacks(runtimeOptions, context);

                try
                {
                    var result = await factory(context, cancellationToken).ConfigureAwait(false);
                    runtimeOptions.Metrics?.RecordMiss(context.Key);
                    return result;
                }
                catch (Exception ex)
                {
                    runtimeOptions.Metrics?.RecordError(context.Key, ex);
                    throw;
                }
            }

            var version = policy.Version;
            var effectiveKeyGenerator = new FixedKeyGenerator(key, version);

            var result = await cacheManager.GetOrCreateAsync(
                FluentMethodName,
                EmptyArgs,
                () => WrappedFactory(),
                policy,
                effectiveKeyGenerator).ConfigureAwait(false);

            if (!factoryExecuted)
            {
                InvokeHitCallbacks(runtimeOptions, context);
                runtimeOptions.Metrics?.RecordHit(context.Key, stopwatch?.Elapsed, result);
            }

            stopwatch?.Stop();

            return result;
        }

        /// <summary>
        /// Attempts to read a cached value without executing a factory.
        /// </summary>
        public static async ValueTask<CacheLookupResult<T>> TryGetAsync<T>(
            this ICacheManager cacheManager,
            string key,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "Cache key cannot be null or whitespace. " +
                    "Provide a unique string identifier for this cache entry. " +
                    "Keys should be descriptive and unique (e.g., 'user:123', 'product:456:details'). " +
                    "Consider using a key generator or manual key construction. " +
                    "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/CONFIGURATION_GUIDE.md#cache-keys",
                    nameof(key));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var policy = CreateRuntimePolicy(FluentMethodName, BuildOptions(null));

            if (cacheManager.TryGetFast<T>(key, out var value))
            {
                return CacheLookupResult<T>.Hit(value!);
            }

            // Fallback to slower path for managers that don't implement TryGetFast.
            value = await cacheManager.TryGetAsync<T>(
                FluentMethodName,
                EmptyArgs,
                policy,
                new FixedKeyGenerator(key)).ConfigureAwait(false);

            if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
            {
                if (EqualityComparer<T>.Default.Equals(value!, default!))
                {
                    var exists = cacheManager.TryGetFast<T>(key, out _);
                    return exists
                        ? CacheLookupResult<T>.Hit(value!)
                        : CacheLookupResult<T>.Miss;
                }

                return CacheLookupResult<T>.Hit(value!);
            }

            return value is null
                ? CacheLookupResult<T>.Miss
                : CacheLookupResult<T>.Hit(value);
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
            var policy = CreateRuntimePolicy(FluentMethodName, options);
            var runtimeOptions = CacheRuntimeOptions.From(options);

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
                var predicateAllowsCaching = options.Predicate == null || options.Predicate(singleContext);

                if (predicateAllowsCaching)
                {
                    var lookup = await cacheManager.TryGetAsync<T>(key, cancellationToken).ConfigureAwait(false);
                    if (lookup.Found)
                    {
                        results[key] = lookup.Value!;
                        InvokeHitCallbacks(runtimeOptions, singleContext);
                        continue;
                    }
                }
                else
                {
                    skipCacheKeys?.Add(key);
                }

                missingKeys.Add(key);
            }

            if (missingKeys.Count > 0)
            {
                var bulkContext = new CacheContext(BulkMethodName, services);
                var produced = await factory(missingKeys, bulkContext, cancellationToken).ConfigureAwait(false);

                var version = policy.Version;

                foreach (var key in missingKeys)
                {
                    if (!produced.TryGetValue(key, out var value))
                    {
                        throw new InvalidOperationException($"Bulk cache factory did not return a value for key '{key}'.");
                    }

                    results[key] = value!;

                    if (skipCacheKeys != null && skipCacheKeys.Contains(key))
                    {
                        continue;
                    }

                    var fixedKeyGenerator = new FixedKeyGenerator(key, version);
                    await cacheManager.GetOrCreateAsync(
                        FluentMethodName,
                        EmptyArgs,
                        () => Task.FromResult(value!),
                        policy,
                        fixedKeyGenerator).ConfigureAwait(false);
                }
            }

            return results;
        }


        /// <summary>
        /// Gets a cached stream or materialises it using the provided factory.
        /// </summary>
        public static async IAsyncEnumerable<T> GetOrCreateStreamAsync<T>(
            this ICacheManager cacheManager,
            string key,
            Func<CacheContext, CancellationToken, IAsyncEnumerable<T>> factory,
            Action<StreamCacheOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key must be provided.", nameof(key));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var streamOptions = BuildStreamOptions(configure);
            var segmentSize = streamOptions.SegmentSize > 0 ? streamOptions.SegmentSize : 100;
            var maxMemorySize = streamOptions.MaxMemorySize > 0 ? streamOptions.MaxMemorySize : 10_000;

            // Try to get cached segments
            var segmentIndex = 0;
            var hasMoreSegments = true;
            var totalItemsRetrieved = 0;

            while (hasMoreSegments && totalItemsRetrieved < maxMemorySize)
            {
                var segmentKey = $"{key}:segment:{segmentIndex}";
                var segment = await cacheManager.TryGetAsync<StreamSegment<T>>(
                    segmentKey,
                    cancellationToken).ConfigureAwait(false);

                if (segment.Found && segment.Value != null)
                {
                    // Yield items from cached segment
                    foreach (var item in segment.Value.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return item;
                        totalItemsRetrieved++;
                        if (totalItemsRetrieved >= maxMemorySize)
                        {
                            yield break;
                        }
                    }
                    hasMoreSegments = segment.Value.HasMore;
                    segmentIndex++;
                }
                else
                {
                    // No cached segment found, need to generate from factory
                    hasMoreSegments = false;
                }
            }

            // If we didn't find any cached segments, generate from factory
            if (segmentIndex == 0)
            {
                segmentIndex = 0;
                var buffer = new List<T>(segmentSize);
                var totalItemsGenerated = 0;

                await foreach (var item in factory(new CacheContext(key, services), cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    buffer.Add(item);
                    totalItemsGenerated++;

                    // When buffer is full or we hit memory limit, cache the segment
                    if (buffer.Count >= segmentSize || totalItemsGenerated >= maxMemorySize)
                    {
                        var hasMore = totalItemsGenerated < maxMemorySize;
                        var segment = new StreamSegment<T>(buffer.ToArray(), hasMore);
                        var segmentKey = $"{key}:segment:{segmentIndex}";

                        // Cache this segment
                        await cacheManager.GetOrCreateAsync(
                            segmentKey,
                            (_, _) => new ValueTask<StreamSegment<T>>(segment),
                            builder =>
                            {
                                if (streamOptions.Duration.HasValue)
                                {
                                    builder.WithDuration(streamOptions.Duration.Value);
                                }
                            },
                            services,
                            cancellationToken).ConfigureAwait(false);

                        // Yield items from this segment
                        foreach (var bufferedItem in buffer)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return bufferedItem;
                        }

                        buffer.Clear();
                        segmentIndex++;

                        if (totalItemsGenerated >= maxMemorySize)
                        {
                            yield break;
                        }
                    }
                }

                // Cache any remaining items in the buffer
                if (buffer.Count > 0)
                {
                    var segment = new StreamSegment<T>(buffer.ToArray(), false);
                    var segmentKey = $"{key}:segment:{segmentIndex}";

                    await cacheManager.GetOrCreateAsync(
                        segmentKey,
                        (_, _) => new ValueTask<StreamSegment<T>>(segment),
                        builder =>
                        {
                            if (streamOptions.Duration.HasValue)
                            {
                                builder.WithDuration(streamOptions.Duration.Value);
                            }
                        },
                        services,
                        cancellationToken).ConfigureAwait(false);

                    // Yield remaining items
                    foreach (var bufferedItem in buffer)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return bufferedItem;
                    }
                }
            }
        }

        private sealed record StreamSegment<T>(IReadOnlyList<T> Items, bool HasMore);

        private static CacheEntryOptions BuildOptions(Action<CacheEntryOptions.Builder>? configure)
        {
            var builder = new CacheEntryOptions.Builder();
            configure?.Invoke(builder);
            return builder.Build();
        }

        private static StreamCacheOptions BuildStreamOptions(Action<StreamCacheOptions.Builder>? configure)
        {
            var builder = new StreamCacheOptions.Builder();
            configure?.Invoke(builder);
            return builder.Build();
        }

private static CacheRuntimePolicy CreateRuntimePolicy(string methodId, CacheEntryOptions options)
{
    if (string.IsNullOrWhiteSpace(methodId))
    {
        throw new ArgumentException("Method id must be provided.", nameof(methodId));
    }

    var (policy, fields) = CachePolicyBuilderFactory.FromOptions(options).BuildPolicy();
    return CacheRuntimePolicy.FromPolicy(methodId, policy, fields);
}

private static void InvokeHitCallbacks(CacheRuntimeOptions options, CacheContext context)
{
    foreach (var callback in options.OnHitCallbacks)
    {
        callback(context);
    }
}

private static void InvokeMissCallbacks(CacheRuntimeOptions options, CacheContext context)
{
    foreach (var callback in options.OnMissCallbacks)
    {
        callback(context);
    }
}


        // Helper classes

        private sealed class FixedKeyGenerator : ICacheKeyGenerator
        {
            private readonly string _key;
            private readonly int? _version;

            public FixedKeyGenerator(string key, int? version = null)
            {
                _key = key ?? throw new ArgumentNullException(nameof(key));
                _version = version;
            }

            public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy)
            {
                var effectiveVersion = _version ?? policy.Version;
                if (effectiveVersion.HasValue)
                {
                    return $"{_key}::v{effectiveVersion.Value}";
                }

                return _key;
            }
        }
    }
}
