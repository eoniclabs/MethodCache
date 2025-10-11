using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration.Runtime;
using MethodCache.Core.Runtime;
using MethodCache.SampleApp.Configuration;
using MethodCache.SampleApp.Infrastructure;
using MethodCache.SampleApp.Services;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp.Runner
{
    internal sealed class SampleScenarioRunner
    {
        private readonly ICacheShowcaseService _service;
        private readonly IRuntimeCacheConfigurator _runtimeConfigurator;
        private readonly EnhancedMetricsProvider _metricsProvider;

        public SampleScenarioRunner(
            ICacheShowcaseService service,
            IRuntimeCacheConfigurator runtimeConfigurator,
            ICacheMetricsProvider metricsProvider)
        {
            _service = service;
            _runtimeConfigurator = runtimeConfigurator;
            _metricsProvider = metricsProvider as EnhancedMetricsProvider
                ?? throw new ArgumentException("EnhancedMetricsProvider is required for the sample", nameof(metricsProvider));
        }

        public async Task RunAsync()
        {
            Console.WriteLine("\n🚀 MethodCache Configuration Showcase\n");
            Console.WriteLine("This sample demonstrates how attribute, fluent, and runtime configuration combine.");
            Console.WriteLine("--------------------------------------------------------------------------\n");

            await PrintEffectiveConfigurationAsync("Initial configuration");

            await DemonstrateAsync("Attribute-based (weather)", () => _service.GetWeatherAsync("London"));
            await DemonstrateAsync("Programmatic (fluent) configuration (pricing)", () => _service.GetPriceAsync("sku-123"));

            Console.WriteLine("\n🛠 Applying runtime override for GetCustomerOrdersAsync...");
            await _runtimeConfigurator.UpsertAsync(
                CacheConfigurationMetadata.ServiceType + "." + nameof(ICacheConfigurationShowcase.GetCustomerOrdersAsync),
                options =>
                {
                    options.WithDuration(TimeSpan.FromSeconds(5))
                           .WithTags("runtime", "orders");
                });

            await PrintEffectiveConfigurationAsync("After runtime override");
            await DemonstrateAsync("Runtime override (orders)", () => _service.GetCustomerOrdersAsync("cust-42"));

            Console.WriteLine("\n🧹 Clearing runtime overrides...");
            await _runtimeConfigurator.ClearAsync();
            await _service.InvalidateTagsAsync("runtime", "orders");
            await PrintEffectiveConfigurationAsync("After clearing runtime overrides");
        }

        private async Task DemonstrateAsync<T>(string title, Func<Task<T>> action)
        {
            Console.WriteLine($"\n=== {title} ===");

            _metricsProvider.ResetMetrics();

            // Warm up cache and demonstrate hit vs miss
            var first = await action();
            var second = await action();

            Console.WriteLine($"Result (first call): {FormatResult(first)}");
            Console.WriteLine($"Result (second call): {FormatResult(second)}");

            PrintMetrics();
        }

        private Task PrintEffectiveConfigurationAsync(string caption)
        {
            Console.WriteLine($"\n📋 {caption}");
            Console.WriteLine(new string('-', caption.Length + 2));

            var settings = _service.GetSettingsSnapshot();
            foreach (var (method, configuration) in settings)
            {
                Console.WriteLine($"{method} -> Duration: {configuration.Duration?.TotalSeconds ?? 0}s, Tags: {string.Join(", ", configuration.Tags)}, Version: {configuration.Version?.ToString() ?? "-"}, Idempotent: {configuration.RequireIdempotent}");
            }

            return Task.CompletedTask;
        }

        private void PrintMetrics()
        {
            var summary = _metricsProvider.GetMetricsSummary();
            if (!summary.MethodMetrics.Any())
            {
                Console.WriteLine("(no cache metrics recorded yet)");
                return;
            }

            foreach (var metrics in summary.MethodMetrics.OrderByDescending(m => m.HitCount + m.MissCount))
            {
                var total = metrics.HitCount + metrics.MissCount;
                var ratio = total > 0 ? (double)metrics.HitCount / total : 0;
                Console.WriteLine($"  {metrics.MethodName}: hits={metrics.HitCount}, misses={metrics.MissCount}, ratio={ratio:P0}");
            }
        }

        private static string FormatResult<T>(T value) => value switch
        {
            Product product => $"Product[{product.SKU}] Price={product.Price:C}",
            decimal price => price.ToString("C"),
            IEnumerable<string> list => string.Join(", ", list),
            _ => value?.ToString() ?? "<null>"
        };
    }
}
