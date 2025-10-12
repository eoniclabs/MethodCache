using MethodCache.Core;
using MethodCache.Core;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp.Configuration
{
    public interface ICacheConfigurationShowcase
    {
        [Cache("attribute-weather", Duration = "00:00:25", Tags = new[] { "attribute", "weather" }, RequireIdempotent = true)]
        Task<string> GetWeatherAsync(string city);

        Task<Product> GetProductAsync(string sku);
        Task<int> GetInventoryLevelAsync(string sku);
        Task<decimal> GetPriceAsync(string sku);
        Task<string> GetCustomerOrdersAsync(string customerId);
    }

    public static class CacheConfigurationMetadata
    {
        public static string ServiceType { get; } = typeof(ICacheConfigurationShowcase).FullName ?? "MethodCache.SampleApp.Configuration.ICacheConfigurationShowcase";
    }
}
