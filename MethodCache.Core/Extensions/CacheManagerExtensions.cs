using System;
using System.Collections.Generic;
using System.Linq;
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

            async Task<T> WrappedFactory()
            {
                factoryExecuted = true;
                InvokeMissCallbacks(options, context);
                return await factory(context, cancellationToken).ConfigureAwait(false);
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
            }

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

            var keyList = keys as IList<string> ?? keys.ToList();
            var results = new Dictionary<string, T>(keyList.Count);
            var missingKeys = new List<string>();

            foreach (var key in keyList)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("Keys must not contain null or whitespace entries.", nameof(keys));
                }

                var lookup = await cacheManager.TryGetAsync<T>(key, cancellationToken).ConfigureAwait(false);
                if (lookup.Found)
                {
                    results[key] = lookup.Value!;
                }
                else
                {
                    missingKeys.Add(key);
                }
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

        private static CacheEntryOptions BuildOptions(Action<CacheEntryOptions.Builder>? configure)
        {
            var builder = new CacheEntryOptions.Builder();
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
                RefreshAhead = options.RefreshAhead
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
