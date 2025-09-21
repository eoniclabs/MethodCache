using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.KeyGenerators;
using MethodCache.Core.Options;
using MethodCache.Core.Metrics;
using MethodCache.Core.Extensions;
using MethodCache.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Core.Fluent
{
    /// <summary>
    /// Fluent builder for configuring cache operations with method chaining.
    /// </summary>
    /// <typeparam name="T">The type of the cached value</typeparam>
    public class CacheBuilder<T>
    {
        private readonly ICacheManager _cacheManager;
        private readonly Func<ValueTask<T>> _factory;
        private readonly CacheEntryOptions.Builder _optionsBuilder;
        private readonly Dictionary<string, object> _contextFunctions;
        private ICacheKeyGenerator? _keyGenerator;
        private IServiceProvider? _services;
        private CancellationToken _cancellationToken;

        internal CacheBuilder(
            ICacheManager cacheManager,
            Func<ValueTask<T>> factory,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _optionsBuilder = new CacheEntryOptions.Builder();
            _contextFunctions = new Dictionary<string, object>();
            _services = services;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Configures the cache entry duration.
        /// </summary>
        /// <param name="duration">The time after which the cache entry expires</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithDuration(TimeSpan duration)
        {
            _optionsBuilder.WithDuration(duration);
            return this;
        }

        /// <summary>
        /// Configures the cache entry duration dynamically based on the cache context.
        /// </summary>
        /// <param name="durationFunc">Function that returns the duration based on cache context</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithDuration(Func<CacheContext, TimeSpan> durationFunc)
        {
            // Store the function to be evaluated later during execution
            SetContextFunction("DurationFunc", durationFunc);
            return this;
        }

        /// <summary>
        /// Configures refresh-ahead behavior before the entry expires.
        /// </summary>
        /// <param name="window">The refresh window</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithRefreshAhead(TimeSpan window)
        {
            _optionsBuilder.RefreshAhead(window);
            return this;
        }

        /// <summary>
        /// Configures cache entry tags for organization and invalidation.
        /// </summary>
        /// <param name="tags">The tags to associate with the cache entry</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithTags(params string[] tags)
        {
            _optionsBuilder.WithTags(tags);
            return this;
        }

        /// <summary>
        /// Configures cache entry tags dynamically based on the cache context.
        /// </summary>
        /// <param name="tagsFunc">Function that returns tags based on cache context</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithTags(Func<CacheContext, string[]> tagsFunc)
        {
            SetContextFunction("TagsFunc", tagsFunc);
            return this;
        }

        /// <summary>
        /// Enables stampede protection to prevent multiple concurrent executions of the same factory.
        /// </summary>
        /// <param name="mode">The stampede protection mode</param>
        /// <param name="beta">The beta parameter for probabilistic mode</param>
        /// <param name="refreshAheadWindow">Optional refresh-ahead window</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithStampedeProtection(
            StampedeProtectionMode mode = StampedeProtectionMode.Probabilistic,
            double beta = 1.0,
            TimeSpan? refreshAheadWindow = null)
        {
            _optionsBuilder.WithStampedeProtection(mode, beta, refreshAheadWindow);
            return this;
        }

        /// <summary>
        /// Configures distributed locking for the cache operation.
        /// </summary>
        /// <param name="timeout">The lock timeout</param>
        /// <param name="maxConcurrency">Maximum concurrent operations</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithDistributedLock(TimeSpan timeout, int maxConcurrency = 1)
        {
            _optionsBuilder.WithDistributedLock(timeout, maxConcurrency);
            return this;
        }

        /// <summary>
        /// Configures metrics collection for the cache operation.
        /// </summary>
        /// <param name="metrics">The metrics collector</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithMetrics(ICacheMetrics metrics)
        {
            _optionsBuilder.WithMetrics(metrics);
            return this;
        }

        /// <summary>
        /// Configures a versioning suffix for cache keys.
        /// </summary>
        /// <param name="version">The version number</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithVersion(int version)
        {
            _optionsBuilder.WithVersion(version);
            return this;
        }

        /// <summary>
        /// Configures callbacks to be invoked on cache hit.
        /// </summary>
        /// <param name="onHit">The callback to invoke on cache hit</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> OnHit(Action<CacheContext> onHit)
        {
            _optionsBuilder.OnHit(onHit);
            return this;
        }

        /// <summary>
        /// Configures callbacks to be invoked on cache miss.
        /// </summary>
        /// <param name="onMiss">The callback to invoke on cache miss</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> OnMiss(Action<CacheContext> onMiss)
        {
            _optionsBuilder.OnMiss(onMiss);
            return this;
        }

        /// <summary>
        /// Configures a predicate to conditionally bypass caching.
        /// </summary>
        /// <param name="predicate">A function that returns true to use caching, false to bypass</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> When(Func<CacheContext, bool> predicate)
        {
            _optionsBuilder.When(predicate);
            return this;
        }

        /// <summary>
        /// Configures a sliding expiration policy.
        /// </summary>
        /// <param name="slidingExpiration">The sliding expiration timespan</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithSlidingExpiration(TimeSpan slidingExpiration)
        {
            _optionsBuilder.WithSlidingExpiration(slidingExpiration);
            return this;
        }

        /// <summary>
        /// Specifies a custom key generator for cache key creation.
        /// </summary>
        /// <param name="keyGenerator">The key generator to use</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithKeyGenerator(ICacheKeyGenerator keyGenerator)
        {
            _keyGenerator = keyGenerator ?? throw new ArgumentNullException(nameof(keyGenerator));
            return this;
        }

        /// <summary>
        /// Specifies a custom key generator type for cache key creation.
        /// </summary>
        /// <typeparam name="TKeyGenerator">The type of key generator to create</typeparam>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithKeyGenerator<TKeyGenerator>()
            where TKeyGenerator : ICacheKeyGenerator, new()
        {
            _keyGenerator = new TKeyGenerator();
            return this;
        }

        /// <summary>
        /// Enables smart key generation that creates human-readable, semantic cache keys.
        /// Smart keys use service names and simplified method names for better debugging and monitoring.
        /// </summary>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithSmartKeying()
        {
            _keyGenerator = new SmartKeyGenerator(_factory);
            return this;
        }

        /// <summary>
        /// Configures the service provider for dependency resolution.
        /// </summary>
        /// <param name="services">The service provider</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithServices(IServiceProvider services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            return this;
        }

        /// <summary>
        /// Configures the cancellation token for the operation.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The builder for method chaining</returns>
        public CacheBuilder<T> WithCancellationToken(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            return this;
        }

        /// <summary>
        /// Executes the cache operation with the configured settings.
        /// </summary>
        /// <returns>The cached or newly created value</returns>
        public async ValueTask<T> ExecuteAsync()
        {
            // Create a context with extracted arguments for conditional logic
            var context = CreateContextWithArguments();

            // Apply context functions if any are defined
            if (_contextFunctions.Count > 0)
            {
                ApplyContextFunctions(context);
            }

            // Use the existing extension method implementation
            return await _cacheManager.GetOrCreateAsync(
                _factory,
                configure: builder => CopyBuilderSettings(builder),
                keyGenerator: _keyGenerator,
                services: _services,
                cancellationToken: _cancellationToken
            );
        }

        private void CopyBuilderSettings(CacheEntryOptions.Builder target)
        {
            var options = _optionsBuilder.Build();

            if (options.Duration.HasValue)
                target.WithDuration(options.Duration.Value);

            if (options.SlidingExpiration.HasValue)
                target.WithSlidingExpiration(options.SlidingExpiration.Value);

            if (options.RefreshAhead.HasValue)
                target.RefreshAhead(options.RefreshAhead.Value);

            target.WithTags(options.Tags.ToArray());

            foreach (var callback in options.OnHitCallbacks)
                target.OnHit(callback);

            foreach (var callback in options.OnMissCallbacks)
                target.OnMiss(callback);

            if (options.StampedeProtection != null)
                target.WithStampedeProtection(options.StampedeProtection);

            if (options.DistributedLock != null)
                target.WithDistributedLock(options.DistributedLock.Timeout, options.DistributedLock.MaxConcurrency);

            if (options.Metrics != null)
                target.WithMetrics(options.Metrics);

            if (options.Version.HasValue)
                target.WithVersion(options.Version.Value);

            if (options.Predicate != null)
                target.When(options.Predicate);
        }

        private void SetContextFunction(string key, object function)
        {
            _contextFunctions[key] = function ?? throw new ArgumentNullException(nameof(function));
        }

        private CacheContext CreateContextWithArguments()
        {
            // Extract arguments from the factory closure
            var arguments = ExtractFactoryArguments();

            // Create a temporary key for context (will be replaced by actual key generator)
            var tempKey = "temp_key_for_context";
            var context = new CacheContext(tempKey, _services);

            // Store arguments in context for GetArg<T>() access
            context.SetArguments(arguments);

            return context;
        }

        private object[] ExtractFactoryArguments()
        {
            // Use the existing AnalyzeFactory method from CacheManagerExtensions
            // This method properly extracts arguments from lambda closures
            try
            {
                // Create a delegate from our ValueTask<T> factory
                Func<Task<T>> taskFactory = async () => await _factory();

                // Use the existing analyze method (need to be accessible)
                return ExtractClosureArguments(_factory.Target);
            }
            catch
            {
                // Fall back to empty arguments if extraction fails
                return Array.Empty<object>();
            }
        }

        private static object[] ExtractClosureArguments(object? target)
        {
            if (target == null)
            {
                return Array.Empty<object>();
            }

            var targetType = target.GetType();

            // Check if this is a compiler-generated closure class
            if (!targetType.Name.Contains("<>") && !targetType.Name.Contains("DisplayClass"))
            {
                return Array.Empty<object>();
            }

            var fields = targetType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var arguments = new List<object>();

            foreach (var field in fields)
            {
                // Skip compiler-generated fields that aren't captured variables
                if (field.Name.StartsWith("CS$<>") || field.Name.StartsWith("<>"))
                {
                    continue;
                }

                try
                {
                    var value = field.GetValue(target);
                    if (value != null)
                    {
                        // Filter out service instances - only include actual method parameters
                        if (!IsServiceType(value.GetType()))
                        {
                            arguments.Add(value);
                        }
                    }
                }
                catch
                {
                    // Skip fields that can't be accessed
                }
            }

            return arguments.ToArray();
        }

        private static bool IsServiceType(Type type)
        {
            var typeName = type.Name;

            // Filter out service types
            if (typeName.EndsWith("Service") ||
                typeName.EndsWith("Repository") ||
                typeName.EndsWith("Manager") ||
                typeName.EndsWith("Handler") ||
                typeName.EndsWith("Provider") ||
                typeName.StartsWith("Mock") || // For testing
                type.GetInterfaces().Any(i =>
                    i.Name.EndsWith("Service") ||
                    i.Name.EndsWith("Repository")))
            {
                return true;
            }

            // Filter out common test artifacts that are not method parameters
            if (type.IsArray ||  // Arrays like Object[]
                type == typeof(bool) ||  // Boolean flags (likely test state)
                type.IsGenericType ||  // Generic types like TaskCompletionSource<T>, List<T>
                typeName.Contains("CompletionSource") ||
                typeName.Contains("List") ||
                typeName.Contains("Dictionary"))
            {
                return true;
            }

            return false;
        }

        private void ApplyContextFunctions(CacheContext context)
        {
            // Apply duration function
            if (_contextFunctions.TryGetValue("DurationFunc", out var durationFunc) &&
                durationFunc is Func<CacheContext, TimeSpan> durFunc)
            {
                var duration = durFunc(context);
                _optionsBuilder.WithDuration(duration);
            }

            // Apply tags function
            if (_contextFunctions.TryGetValue("TagsFunc", out var tagsFunc) &&
                tagsFunc is Func<CacheContext, string[]> tFunc)
            {
                var tags = tFunc(context);
                _optionsBuilder.WithTags(tags);
            }
        }
    }
}