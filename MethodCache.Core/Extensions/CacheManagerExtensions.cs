using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;

namespace MethodCache.Core.Extensions
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
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key must be provided.", nameof(key));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var options = BuildOptions(configure);
            var settings = ToMethodSettings(options);
            var context = new CacheContext(key, services);
            var factoryExecuted = false;
            var stopwatch = options.Metrics != null ? Stopwatch.StartNew() : null;

            if (options.Predicate != null && !options.Predicate(context))
            {
                var bypassResult = await factory(context, cancellationToken).ConfigureAwait(false);
                options.Metrics?.RecordMiss(context.Key);
                return bypassResult;
            }

            async Task<T> WrappedFactory()
            {
                factoryExecuted = true;
                InvokeMissCallbacks(options, context);

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

            var result = await cacheManager.GetOrCreateAsync(
                FluentMethodName,
                EmptyArgs,
                () => WrappedFactory(),
                settings,
                new FixedKeyGenerator(key),
                requireIdempotent: true).ConfigureAwait(false);

            if (!factoryExecuted)
            {
                InvokeHitCallbacks(options, context);
                options.Metrics?.RecordHit(context.Key, stopwatch?.Elapsed, result);
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
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key must be provided.", nameof(key));

            cancellationToken.ThrowIfCancellationRequested();

            var value = await cacheManager.TryGetAsync<T>(
                FluentMethodName,
                EmptyArgs,
                new CacheMethodSettings(),
                new FixedKeyGenerator(key)).ConfigureAwait(false);

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
            var keyList = keys as IList<string> ?? keys.ToList();
            var results = new Dictionary<string, T>(keyList.Count);
            var missingKeys = new List<string>();
            HashSet<string>? skipCacheKeys = null;
            if (options.Predicate != null)
            {
                skipCacheKeys = new HashSet<string>(StringComparer.Ordinal);
            }

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

                foreach (var key in missingKeys)
                {
                    if (!produced.TryGetValue(key, out var value))
                    {
                        throw new InvalidOperationException($"Bulk cache factory did not return a value for key '{key}'.");
                    }

                    var capturedValue = value!;
                    results[key] = capturedValue;

                    if (skipCacheKeys != null && skipCacheKeys.Contains(key))
                    {
                        continue;
                    }

                    await cacheManager.GetOrCreateAsync(
                        key,
                        (_, _) => new ValueTask<T>(capturedValue),
                        configure,
                        services,
                        cancellationToken).ConfigureAwait(false);
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
            var segmentCapacity = streamOptions.SegmentSize > 0 ? streamOptions.SegmentSize : 0;

            var items = await cacheManager.GetOrCreateAsync<IReadOnlyList<T>>(
                key,
                async (context, token) =>
                {
                    var buffer = segmentCapacity > 0 ? new List<T>(segmentCapacity) : new List<T>();
                    await foreach (var item in factory(context, token).WithCancellation(token).ConfigureAwait(false))
                    {
                        buffer.Add(item);
                    }

                    return (IReadOnlyList<T>)buffer.ToArray();
                },
                builder =>
                {
                    if (streamOptions.Duration.HasValue)
                    {
                        builder.WithDuration(streamOptions.Duration.Value);
                    }
                },
                services,
                cancellationToken).ConfigureAwait(false);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

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

        private static CacheMethodSettings ToMethodSettings(CacheEntryOptions options)
        {
            var settings = new CacheMethodSettings
            {
                Duration = options.Duration,
                IsIdempotent = true,
                Tags = new List<string>(options.Tags),
                SlidingExpiration = options.SlidingExpiration,
                RefreshAhead = options.RefreshAhead,
                StampedeProtection = options.StampedeProtection,
                DistributedLock = options.DistributedLock,
                Metrics = options.Metrics
            };

            return settings;
        }

        private static void InvokeHitCallbacks(CacheEntryOptions options, CacheContext context)
        {
            foreach (var callback in options.OnHitCallbacks)
            {
                callback(context);
            }
        }

        private static void InvokeMissCallbacks(CacheEntryOptions options, CacheContext context)
        {
            foreach (var callback in options.OnMissCallbacks)
            {
                callback(context);
            }
        }

        private sealed class FixedKeyGenerator : ICacheKeyGenerator
        {
            private readonly string _key;

            public FixedKeyGenerator(string key)
            {
                _key = key ?? throw new ArgumentNullException(nameof(key));
            }

            public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings) => _key;
        }
    }
}
