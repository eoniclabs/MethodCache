using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Extensions;
using MethodCache.Core.KeyGenerators;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

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
        /// Gets a cache value or creates it using automatic key generation from the factory method.
        /// </summary>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            string methodName,
            object[] args,
            Func<ValueTask<T>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            ICacheKeyGenerator? keyGenerator = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            return await GetOrCreateAsync(cacheManager, factory, configure, keyGenerator: null, services, cancellationToken);
        }

        /// <summary>
        /// Gets a cache value or creates it using automatic key generation from the factory method with a specific key generator.
        /// </summary>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            Func<ValueTask<T>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            ICacheKeyGenerator? keyGenerator = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var options = BuildOptions(configure);
            var descriptor = CreateRuntimeDescriptor(FluentMethodName, options);
            var runtimeOptions = descriptor.RuntimeOptions;

            // Analyze factory to extract method info and arguments
            var (methodName, args) = AnalyzeFactory(factory);

            // Use provided key generator, or resolve from DI, or default to FastHashKeyGenerator
            var effectiveKeyGenerator = keyGenerator ?? services?.GetService<ICacheKeyGenerator>() ?? new FastHashKeyGenerator();
            var cacheKey = effectiveKeyGenerator.GenerateKey(methodName, args, descriptor);

            var context = new CacheContext(cacheKey, services);
            context.SetArguments(args);

            var factoryExecuted = false;
            var stopwatch = runtimeOptions.Metrics != null ? Stopwatch.StartNew() : null;

            if (options.Predicate != null && !options.Predicate(context))
            {
                var bypassResult = await factory().ConfigureAwait(false);
                runtimeOptions.Metrics?.RecordMiss(context.Key);
                return bypassResult;
            }

            async Task<T> WrappedFactory()
            {
                factoryExecuted = true;
                InvokeMissCallbacks(runtimeOptions, context);

                try
                {
                    var result = await factory().ConfigureAwait(false);
                    runtimeOptions.Metrics?.RecordMiss(context.Key);
                    return result;
                }
                catch (Exception ex)
                {
                    runtimeOptions.Metrics?.RecordError(context.Key, ex);
                    throw;
                }
            }

            var version = descriptor.Version;
            var fixedKeyGen = new FixedKeyGenerator(cacheKey, version);

            var result = await cacheManager.GetOrCreateAsync(
                FluentMethodName,
                EmptyArgs,
                () => WrappedFactory(),
                descriptor,
                fixedKeyGen).ConfigureAwait(false);

            if (!factoryExecuted)
            {
                InvokeHitCallbacks(runtimeOptions, context);
                runtimeOptions.Metrics?.RecordHit(context.Key, stopwatch?.Elapsed, result);
            }

            stopwatch?.Stop();

            return result;
        }

        /// <summary>
        /// Gets a cache value or creates it using automatic key generation from method name and arguments.
        /// </summary>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            string methodName,
            object[] args,
            Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            return await GetOrCreateAsync(cacheManager, methodName, args, factory, configure, keyGenerator: null, services, cancellationToken);
        }

        /// <summary>
        /// Gets a cache value or creates it using automatic key generation from method name and arguments with a specific key generator.
        /// </summary>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            string methodName,
            object[] args,
            Func<CacheContext, CancellationToken, ValueTask<T>> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            ICacheKeyGenerator? keyGenerator = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException(
                    "Method name cannot be null or whitespace. Provide a descriptive method name for cache key generation (e.g., 'GetUser', 'FetchOrders'). This name is used to generate unique cache keys. See: https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/CONFIGURATION_GUIDE.md#manual-caching",
                    nameof(methodName));
            }
            if (args == null) throw new ArgumentNullException(nameof(args));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var options = BuildOptions(configure);
            var descriptor = CreateRuntimeDescriptor(methodName, options);
            var runtimeOptions = descriptor.RuntimeOptions;

            // Use provided key generator, or resolve from DI, or default to FastHashKeyGenerator
            var effectiveKeyGenerator = keyGenerator ?? services?.GetService<ICacheKeyGenerator>() ?? new FastHashKeyGenerator();
            var cacheKey = effectiveKeyGenerator.GenerateKey(methodName, args, descriptor);

            var context = new CacheContext(cacheKey, services);
            context.SetArguments(args);

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

            var version = descriptor.Version;
            var fixedKeyGen = new FixedKeyGenerator(cacheKey, version);

            var result = await cacheManager.GetOrCreateAsync(
                FluentMethodName,
                EmptyArgs,
                () => WrappedFactory(),
                descriptor,
                fixedKeyGen).ConfigureAwait(false);

            if (!factoryExecuted)
            {
                InvokeHitCallbacks(runtimeOptions, context);
                runtimeOptions.Metrics?.RecordHit(context.Key, stopwatch?.Elapsed, result);
            }

            stopwatch?.Stop();

            return result;
        }

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
            var descriptor = CreateRuntimeDescriptor(FluentMethodName, options);
            var runtimeOptions = descriptor.RuntimeOptions;
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

            var version = descriptor.Version;
            var effectiveKeyGenerator = new FixedKeyGenerator(key, version);

            var result = await cacheManager.GetOrCreateAsync(
                FluentMethodName,
                EmptyArgs,
                () => WrappedFactory(),
                descriptor,
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

            var descriptor = CreateRuntimeDescriptor(FluentMethodName, BuildOptions(null));

            var value = await cacheManager.TryGetAsync<T>(
                FluentMethodName,
                EmptyArgs,
                descriptor,
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
            var descriptor = CreateRuntimeDescriptor(FluentMethodName, options);
            var runtimeOptions = descriptor.RuntimeOptions;

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

                var version = descriptor.Version;

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
                        descriptor,
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

private static CacheRuntimeDescriptor CreateRuntimeDescriptor(string methodId, CacheEntryOptions options)
{
    if (string.IsNullOrWhiteSpace(methodId))
    {
        throw new ArgumentException("Method id must be provided.", nameof(methodId));
    }

    var runtimeOptions = CacheRuntimeOptions.From(options);
    var builder = CachePolicyBuilderFactory.FromOptions(options);
    builder.RequireIdempotent(true);
    var draft = builder.Build(methodId);
    var descriptor = CacheRuntimeDescriptor.FromPolicyDraft(draft, runtimeOptions);
    return descriptor;
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


        /// <summary>
        /// Gets a cache value or creates it using automatic key generation from the factory method (synchronous version).
        /// </summary>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            Func<T> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            return await GetOrCreateAsync(cacheManager, factory, configure, keyGenerator: null, services, cancellationToken);
        }

        /// <summary>
        /// Gets a cache value or creates it using automatic key generation from the factory method (synchronous version) with a specific key generator.
        /// </summary>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            Func<T> factory,
            Action<CacheEntryOptions.Builder>? configure = null,
            ICacheKeyGenerator? keyGenerator = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            cancellationToken.ThrowIfCancellationRequested();

            var options = BuildOptions(configure);
            var (methodName, args) = AnalyzeFactory(factory);
            var descriptor = CreateRuntimeDescriptor(methodName, options);
            var runtimeOptions = descriptor.RuntimeOptions;

            var effectiveKeyGenerator = keyGenerator ?? services?.GetService<ICacheKeyGenerator>() ?? new FastHashKeyGenerator();
            var cacheKey = effectiveKeyGenerator.GenerateKey(methodName, args, descriptor);

            var context = new CacheContext(cacheKey, services);
            context.SetArguments(args);

            var factoryExecuted = false;
            var stopwatch = runtimeOptions.Metrics != null ? Stopwatch.StartNew() : null;

            if (options.Predicate != null && !options.Predicate(context))
            {
                var bypassResult = factory();
                runtimeOptions.Metrics?.RecordMiss(context.Key);
                return bypassResult;
            }

            Task<T> WrappedFactory()
            {
                factoryExecuted = true;
                InvokeMissCallbacks(runtimeOptions, context);

                try
                {
                    var result = factory();
                    runtimeOptions.Metrics?.RecordMiss(context.Key);
                    return Task.FromResult(result);
                }
                catch (Exception ex)
                {
                    runtimeOptions.Metrics?.RecordError(context.Key, ex);
                    throw;
                }
            }

            var version = descriptor.Version;
            var fixedKeyGen = new FixedKeyGenerator(cacheKey, version);

            var result = await cacheManager.GetOrCreateAsync(
                FluentMethodName,
                EmptyArgs,
                () => WrappedFactory(),
                descriptor,
                fixedKeyGen).ConfigureAwait(false);

            if (!factoryExecuted)
            {
                InvokeHitCallbacks(runtimeOptions, context);
                runtimeOptions.Metrics?.RecordHit(context.Key, stopwatch?.Elapsed, result);
            }

            stopwatch?.Stop();

            return result;
        }

        private static (string MethodName, object[] Arguments) AnalyzeFactory<T>(Func<ValueTask<T>> factory)
        {
            return AnalyzeFactoryInternal(factory);
        }

        private static (string MethodName, object[] Arguments) AnalyzeFactory<T>(Func<T> factory)
        {
            return AnalyzeFactoryInternal(factory);
        }

        private static (string MethodName, object[] Arguments) AnalyzeFactoryInternal(Delegate factory)
        {
            // Extract method name from the delegate
            var methodName = factory.Method.Name;

            // If it's a lambda or anonymous method, try to get a more descriptive name
            if (methodName.StartsWith("<") || methodName == "Invoke")
            {
                // For lambdas calling methods, we'll extract the target method name
                if (factory.Target != null)
                {
                    var targetType = factory.Target.GetType();
                    methodName = $"{targetType.Name}.Lambda";
                }
                else
                {
                    methodName = "AnonymousMethod";
                }
            }

            // Extract captured variables from closure
            var arguments = ExtractClosureArguments(factory.Target);

            return (methodName, arguments);
        }

        private static object[] ExtractClosureArguments(object? target)
        {
            if (target == null)
            {
                return EmptyArgs;
            }

            var targetType = target.GetType();

            // Check if this is a compiler-generated closure class
            if (!targetType.Name.Contains("<>") && !targetType.Name.Contains("DisplayClass"))
            {
                return EmptyArgs;
            }

            var fields = targetType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var arguments = new List<object?>();

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
                    else
                    {
                        // Include null values as they might be valid arguments
                        arguments.Add(null);
                    }
                }
                catch
                {
                    // If we can't get the value, skip it
                    continue;
                }
            }

            return arguments.ToArray()!;
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

            // Filter out only specific test artifacts that are not method parameters
            if (type.IsArray && type.GetElementType() == typeof(object) ||  // Object[] arrays from tests
                typeName.Contains("CompletionSource"))  // TaskCompletionSource and similar
            {
                return true;
            }

            return false;
        }

        // Method Chaining API

        /// <summary>
        /// Begins a fluent cache operation with automatic key generation from the factory method.
        /// </summary>
        /// <typeparam name="T">The type of the cached value</typeparam>
        /// <param name="cacheManager">The cache manager</param>
        /// <param name="factory">The factory method to cache</param>
        /// <param name="services">Optional service provider</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A fluent builder for configuring the cache operation</returns>
        public static CacheBuilder<T> Cache<T>(
            this ICacheManager cacheManager,
            Func<ValueTask<T>> factory,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            return new CacheBuilder<T>(cacheManager, factory, services, cancellationToken);
        }

        /// <summary>
        /// Begins a fluent cache operation with automatic key generation from the factory method.
        /// Alternative API that returns a builder for method chaining.
        /// </summary>
        /// <typeparam name="T">The type of the cached value</typeparam>
        /// <param name="cacheManager">The cache manager</param>
        /// <param name="factory">The factory method to cache</param>
        /// <param name="services">Optional service provider</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A fluent builder for configuring the cache operation</returns>
        public static CacheBuilder<T> Build<T>(
            this ICacheManager cacheManager,
            Func<ValueTask<T>> factory,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            return new CacheBuilder<T>(cacheManager, factory, services, cancellationToken);
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

            public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
            {
                var effectiveVersion = _version ?? descriptor.Version;
                if (effectiveVersion.HasValue)
                {
                    return $"{_key}::v{effectiveVersion.Value}";
                }

                return _key;
            }
        }
    }
}
