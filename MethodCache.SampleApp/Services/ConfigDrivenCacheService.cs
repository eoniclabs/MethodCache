using System;
using System.Collections.Generic;
using MethodCache.Abstractions.Registry;
using MethodCache.Abstractions.Policies;
using MethodCache.Core;
using MethodCache.Core.Runtime;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.SampleApp.Configuration;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp.Services
{
    public interface ICacheShowcaseService
    {
        Task<string> GetWeatherAsync(string city);
        Task<Product> GetProductAsync(string sku);
        Task<int> GetInventoryLevelAsync(string sku);
        Task<decimal> GetPriceAsync(string sku);
        Task<string> GetCustomerOrdersAsync(string customerId);
        IReadOnlyDictionary<string, CacheRuntimePolicy> GetSettingsSnapshot();
        Task InvalidateTagsAsync(params string[] tags);
    }

    internal sealed class ConfigDrivenCacheService : ICacheShowcaseService
    {
        private static readonly string ServiceType = CacheConfigurationMetadata.ServiceType;

        private static readonly string[] MethodNames =
        {
            nameof(ICacheConfigurationShowcase.GetWeatherAsync),
            nameof(ICacheConfigurationShowcase.GetProductAsync),
            nameof(ICacheConfigurationShowcase.GetInventoryLevelAsync),
            nameof(ICacheConfigurationShowcase.GetPriceAsync),
            nameof(ICacheConfigurationShowcase.GetCustomerOrdersAsync)
        };

        private readonly ICacheManager _cacheManager;
        private readonly ICacheKeyGenerator _keyGenerator;
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly IPolicyRegistry _policyRegistry;
        private readonly Dictionary<string, CacheRuntimePolicy> _defaults;
        private readonly Random _random = new();

        public ConfigDrivenCacheService(
            ICacheManager cacheManager,
            ICacheKeyGenerator keyGenerator,
            ICacheMetricsProvider metricsProvider,
            IPolicyRegistry policyRegistry)
        {
            _cacheManager = cacheManager;
            _keyGenerator = keyGenerator;
            _metricsProvider = metricsProvider;
            _policyRegistry = policyRegistry;

            _defaults = new Dictionary<string, CacheRuntimePolicy>(StringComparer.Ordinal)
            {
                [nameof(ICacheConfigurationShowcase.GetWeatherAsync)] = CreateDefaultDescriptor(
                    nameof(ICacheConfigurationShowcase.GetWeatherAsync),
                    TimeSpan.FromSeconds(15),
                    new[] { "default", "weather" },
                    true),
                [nameof(ICacheConfigurationShowcase.GetProductAsync)] = CreateDefaultDescriptor(
                    nameof(ICacheConfigurationShowcase.GetProductAsync),
                    TimeSpan.FromMinutes(5),
                    new[] { "default", "product" },
                    true),
                [nameof(ICacheConfigurationShowcase.GetInventoryLevelAsync)] = CreateDefaultDescriptor(
                    nameof(ICacheConfigurationShowcase.GetInventoryLevelAsync),
                    TimeSpan.FromMinutes(2),
                    new[] { "default", "inventory" },
                    true),
                [nameof(ICacheConfigurationShowcase.GetPriceAsync)] = CreateDefaultDescriptor(
                    nameof(ICacheConfigurationShowcase.GetPriceAsync),
                    TimeSpan.FromMinutes(10),
                    new[] { "default", "pricing" },
                    true),
                [nameof(ICacheConfigurationShowcase.GetCustomerOrdersAsync)] = CreateDefaultDescriptor(
                    nameof(ICacheConfigurationShowcase.GetCustomerOrdersAsync),
                    TimeSpan.FromMinutes(1),
                    new[] { "default", "orders" },
                    true)
            };
        }

        private static CacheRuntimePolicy CreateDefaultDescriptor(string methodName, TimeSpan duration, string[] tags, bool requireIdempotent)
        {
            var policy = CachePolicy.Empty with
            {
                Duration = duration,
                Tags = tags,
                RequireIdempotent = requireIdempotent
            };
            return CacheRuntimePolicy.FromPolicy(
                methodName,
                policy,
                CachePolicyFields.Duration | CachePolicyFields.Tags | CachePolicyFields.RequireIdempotent);
        }

        public Task<string> GetWeatherAsync(string city) =>
            ExecuteWithCacheAsync(
                nameof(ICacheConfigurationShowcase.GetWeatherAsync),
                new object[] { city },
                async () =>
                {
                    Console.WriteLine($"[MISS] Fetching weather for {city}...");
                    await Task.Delay(300);
                    return $"{city} {GenerateTemperature()}°C @ {DateTime.Now:HH:mm:ss}";
                });

        public Task<Product> GetProductAsync(string sku) =>
            ExecuteWithCacheAsync(
                nameof(ICacheConfigurationShowcase.GetProductAsync),
                new object[] { sku },
                async () =>
                {
                    Console.WriteLine($"[MISS] Loading product {sku} from database...");
                    await Task.Delay(400);
                    return new Product
                    {
                        SKU = sku,
                        Name = $"Product {sku.ToUpperInvariant()}",
                        Description = "Configured via appsettings.json",
                        Price = Math.Round((decimal)_random.NextDouble() * 100m, 2),
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 90)),
                        UpdatedAt = DateTime.UtcNow
                    };
                });

        public Task<int> GetInventoryLevelAsync(string sku) =>
            ExecuteWithCacheAsync(
                nameof(ICacheConfigurationShowcase.GetInventoryLevelAsync),
                new object[] { sku },
                async () =>
                {
                    Console.WriteLine($"[MISS] Querying warehouse stock for {sku}...");
                    await Task.Delay(250);
                    return _random.Next(0, 500);
                });

        public Task<decimal> GetPriceAsync(string sku) =>
            ExecuteWithCacheAsync(
                nameof(ICacheConfigurationShowcase.GetPriceAsync),
                new object[] { sku },
                async () =>
                {
                    Console.WriteLine($"[MISS] Calculating price for {sku}...");
                    await Task.Delay(350);
                    var basePrice = Math.Round((decimal)_random.NextDouble() * 120m, 2);
                    return basePrice + (sku.GetHashCode() % 5);
                });

        public Task<string> GetCustomerOrdersAsync(string customerId) =>
            ExecuteWithCacheAsync(
                nameof(ICacheConfigurationShowcase.GetCustomerOrdersAsync),
                new object[] { customerId },
                async () =>
                {
                    Console.WriteLine($"[MISS] Fetching orders for customer {customerId}...");
                    await Task.Delay(500);
                    return $"Orders for {customerId}: {string.Join(", ", GenerateOrders())}";
                });

        public IReadOnlyDictionary<string, CacheRuntimePolicy> GetSettingsSnapshot()
        {
            var snapshot = new Dictionary<string, CacheRuntimePolicy>(StringComparer.Ordinal);

            foreach (var method in MethodNames)
            {
                snapshot[method] = ResolveSettings(method);
            }

            return snapshot;
        }

        public Task InvalidateTagsAsync(params string[] tags)
        {
            if (tags.Length == 0)
            {
                return Task.CompletedTask;
            }

            Console.WriteLine($"[INVALIDATE] Clearing tags: {string.Join(", ", tags)}");
            return _cacheManager.InvalidateByTagsAsync(tags);
        }

        private async Task<T> ExecuteWithCacheAsync<T>(string methodName, object[] args, Func<Task<T>> factory)
        {
            var descriptor = ResolveSettings(methodName);
            var key = _keyGenerator.GenerateKey(methodName, args, descriptor);

            var result = await _cacheManager.GetOrCreateAsync(
                methodName,
                args,
                async () =>
                {
                    var value = await factory();
                    return value;
                },
                descriptor,
                _keyGenerator);

            Console.WriteLine($"[HIT] {methodName} key={key} (duration: {descriptor.Duration?.TotalSeconds ?? 0}s, tags: {string.Join(", ", descriptor.Tags)})");
            return result;
        }

        private CacheRuntimePolicy ResolveSettings(string methodName)
        {
            var methodId = $"{ServiceType}.{methodName}";
            var policyResult = _policyRegistry.GetPolicy(methodId);
            var policy = policyResult.Policy;

            var hasConfiguredValues =
                policy.Duration.HasValue ||
                policy.Tags.Count > 0 ||
                policy.KeyGeneratorType != null ||
                policy.Version.HasValue ||
                policy.RequireIdempotent.HasValue ||
                policy.Metadata.Count > 0;

            if (hasConfiguredValues)
            {
                return CacheRuntimePolicy.FromResolverResult(policyResult);
            }

            if (_defaults.TryGetValue(methodName, out var fallbackDefault))
            {
                return fallbackDefault;
            }

            // Final fallback
            var fallbackPolicy = CachePolicy.Empty with
            {
                Duration = TimeSpan.FromSeconds(30),
                Tags = new[] { "default" },
                RequireIdempotent = true
            };
            return CacheRuntimePolicy.FromPolicy(
                methodName,
                fallbackPolicy,
                CachePolicyFields.Duration | CachePolicyFields.Tags | CachePolicyFields.RequireIdempotent);
        }

        private int GenerateTemperature() => _random.Next(-5, 35);

        private IEnumerable<string> GenerateOrders()
        {
            var orderCount = _random.Next(1, 4);
            for (var i = 0; i < orderCount; i++)
            {
                yield return $"ORD-{_random.Next(1000, 9999)}";
            }
        }
    }
}