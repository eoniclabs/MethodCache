using System;
using System.Collections.Generic;
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
        private static readonly object[] EmptyArgs = Array.Empty<object>();

        /// <summary>
        /// Gets a cache value or creates it using the provided factory, using a fluent configuration surface.
        /// </summary>
        public static ValueTask<T> GetOrCreateAsync<T>(
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

            return new ValueTask<T>(cacheManager.GetOrCreateAsync(
                FluentMethodName,
                EmptyArgs,
                () => InvokeFactoryAsync(factory, context, cancellationToken),
                settings,
                new FixedKeyGenerator(key),
                requireIdempotent: true));
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

        private static Task<T> InvokeFactoryAsync<T>(
            Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            CacheContext context,
            CancellationToken cancellationToken)
        {
            var task = factory(context, cancellationToken);
            return task.AsTask();
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
                Tags = new List<string>(options.Tags)
            };

            return settings;
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
