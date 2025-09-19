using System.Collections.Generic;
using MethodCache.Core;
using MethodCache.Core.Configuration;
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
        IReadOnlyDictionary<string, CacheMethodSettings> GetSettingsSnapshot();
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
        private readonly IMethodCacheConfigurationManager _configurationManager;
        private readonly Dictionary<string, CacheMethodSettings> _defaults;
        private readonly Random _random = new();

        public ConfigDrivenCacheService(
            ICacheManager cacheManager,
            ICacheKeyGenerator keyGenerator,
            ICacheMetricsProvider metricsProvider,
            IMethodCacheConfigurationManager configurationManager)
        {
            _cacheManager = cacheManager;
            _keyGenerator = keyGenerator;
            _metricsProvider = metricsProvider;
            _configurationManager = configurationManager;

            _defaults = new Dictionary<string, CacheMethodSettings>(StringComparer.Ordinal)
            {
                [nameof(ICacheConfigurationShowcase.GetWeatherAsync)] = new CacheMethodSettings
                {
                    Duration = TimeSpan.FromSeconds(15),
                    Tags = new List<string> { "default", "weather" },
                    IsIdempotent = true
                },
                [nameof(ICacheConfigurationShowcase.GetProductAsync)] = new CacheMethodSettings
                {
                    Duration = TimeSpan.FromMinutes(5),
                    Tags = new List<string> { "default", "product" },
                    IsIdempotent = true
                },
                [nameof(ICacheConfigurationShowcase.GetInventoryLevelAsync)] = new CacheMethodSettings
                {
                    Duration = TimeSpan.FromMinutes(2),
                    Tags = new List<string> { "default", "inventory" },
                    IsIdempotent = true
                },
                [nameof(ICacheConfigurationShowcase.GetPriceAsync)] = new CacheMethodSettings
                {
                    Duration = TimeSpan.FromMinutes(10),
                    Tags = new List<string> { "default", "pricing" },
                    IsIdempotent = true
                },
                [nameof(ICacheConfigurationShowcase.GetCustomerOrdersAsync)] = new CacheMethodSettings
                {
                    Duration = TimeSpan.FromMinutes(1),
                    Tags = new List<string> { "default", "orders" },
                    IsIdempotent = true
                }
            };
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

        public IReadOnlyDictionary<string, CacheMethodSettings> GetSettingsSnapshot()
        {
            var snapshot = new Dictionary<string, CacheMethodSettings>(StringComparer.Ordinal);

            foreach (var method in MethodNames)
            {
                snapshot[method] = ResolveSettings(method).Clone();
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
            var settings = ResolveSettings(methodName);
            var key = _keyGenerator.GenerateKey(methodName, args, settings);

            var result = await _cacheManager.GetOrCreateAsync(
                methodName,
                args,
                async () =>
                {
                    var value = await factory();
                    return value;
                },
                settings,
                _keyGenerator,
                settings.IsIdempotent);

            Console.WriteLine($"[HIT] {methodName} key={key} (duration: {settings.Duration?.TotalSeconds ?? 0}s, tags: {string.Join(", ", settings.Tags)})");
            return result;
        }

        private CacheMethodSettings ResolveSettings(string methodName)
        {
            var configured = _configurationManager.GetMethodConfiguration(ServiceType, methodName)?.Clone();

            if (configured == null && _defaults.TryGetValue(methodName, out var fallbackDefault))
            {
                return fallbackDefault.Clone();
            }

            if (configured == null)
            {
                return new CacheMethodSettings
                {
                    Duration = TimeSpan.FromSeconds(30),
                    Tags = new List<string> { "default" },
                    IsIdempotent = true
                };
            }

            if (_defaults.TryGetValue(methodName, out var defaults))
            {
                configured.Duration ??= defaults.Duration;
                configured.Version ??= defaults.Version;

                if (configured.Tags.Count == 0 && defaults.Tags.Count > 0)
                {
                    configured.Tags = new List<string>(defaults.Tags);
                }

                if (!configured.IsIdempotent)
                {
                    configured.IsIdempotent = defaults.IsIdempotent;
                }
            }

            configured.Duration ??= TimeSpan.FromSeconds(30);
            if (configured.Tags.Count == 0)
            {
                configured.Tags.Add("default");
            }

            return configured;
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
