using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Tests.ThirdParty
{
    /// <summary>
    /// Integration tests for third-party library caching approaches from CONFIGURATION_GUIDE.md
    /// These tests verify configuration loading and validation, not actual caching behavior.
    /// </summary>
    public class ThirdPartyConfigurationIntegrationTests
    {
        #region Test Interfaces (Simulating Third-Party APIs)

        // Simulate Stripe API
        public interface IStripeClient
        {
            Task<Customer> GetCustomerAsync(string customerId);
            Task<PaymentIntent> CreatePaymentIntentAsync(PaymentIntentCreateRequest request);
            Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId);
        }

        // Simulate Weather API
        public interface IWeatherApiClient
        {
            Task<CurrentWeather> GetCurrentWeatherAsync(string city);
            Task<WeatherForecast> GetForecastAsync(string city, int days);
        }

        #endregion

        #region Test Models

        public record Customer(string Id, string Email, string Name);
        public record PaymentIntent(string Id, string Status, decimal Amount);
        public record PaymentIntentCreateRequest(decimal Amount, string Currency);
        public record CurrentWeather(string City, double Temperature, string Description);
        public record WeatherForecast(string City, WeatherDay[] Days);
        public record WeatherDay(DateTime Date, double Temperature, string Description);

        #endregion

        [Fact]
        public async Task JsonConfiguration_ThirdPartyServices_ShouldLoadCorrectly()
        {
            // Arrange - Simulate production JSON configuration for multiple third-party services
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    // Stripe API Configuration - Cache read operations, never cache writes
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Duration"] = "01:00:00",
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Tags:0"] = "stripe",
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Tags:1"] = "customer",
                    
                    ["MethodCache:Services:IStripeClient.GetPaymentIntentAsync:Duration"] = "00:30:00",
                    ["MethodCache:Services:IStripeClient.GetPaymentIntentAsync:Tags:0"] = "stripe",
                    ["MethodCache:Services:IStripeClient.GetPaymentIntentAsync:Tags:1"] = "payment",

                    // Weather API Configuration - Different durations based on data volatility
                    ["MethodCache:Services:IWeatherApiClient.GetCurrentWeatherAsync:Duration"] = "00:10:00",
                    ["MethodCache:Services:IWeatherApiClient.GetCurrentWeatherAsync:Tags:0"] = "weather",
                    ["MethodCache:Services:IWeatherApiClient.GetCurrentWeatherAsync:Tags:1"] = "current",

                    ["MethodCache:Services:IWeatherApiClient.GetForecastAsync:Duration"] = "01:00:00",
                    ["MethodCache:Services:IWeatherApiClient.GetForecastAsync:Tags:0"] = "weather", 
                    ["MethodCache:Services:IWeatherApiClient.GetForecastAsync:Tags:1"] = "forecast"
                })
                .Build();

            var configManager = new MethodCache.Core.Configuration.ConfigurationManager(null);
            configManager.AddSource(new JsonConfigurationSource(configuration));

            // Act
            await configManager.LoadConfigurationAsync();

            // Assert - Stripe Configuration
            var stripeCustomer = configManager.GetMethodConfiguration("IStripeClient", "GetCustomerAsync");
            Assert.NotNull(stripeCustomer);
            Assert.Equal(TimeSpan.FromHours(1), stripeCustomer.Duration);
            Assert.Contains("stripe", stripeCustomer.Tags);
            Assert.Contains("customer", stripeCustomer.Tags);

            var stripePayment = configManager.GetMethodConfiguration("IStripeClient", "GetPaymentIntentAsync");
            Assert.NotNull(stripePayment);
            Assert.Equal(TimeSpan.FromMinutes(30), stripePayment.Duration);

            // Write operations like CreatePaymentIntentAsync should not be configured
            var stripeCreatePayment = configManager.GetMethodConfiguration("IStripeClient", "CreatePaymentIntentAsync");
            Assert.Null(stripeCreatePayment); // No configuration = no caching for write operations

            // Assert - Weather API Configuration  
            var weatherCurrent = configManager.GetMethodConfiguration("IWeatherApiClient", "GetCurrentWeatherAsync");
            Assert.NotNull(weatherCurrent);
            Assert.Equal(TimeSpan.FromMinutes(10), weatherCurrent.Duration);

            var weatherForecast = configManager.GetMethodConfiguration("IWeatherApiClient", "GetForecastAsync");
            Assert.NotNull(weatherForecast);
            Assert.Equal(TimeSpan.FromHours(1), weatherForecast.Duration);
        }

        [Fact]
        public async Task ProgrammaticConfiguration_ThirdPartyServices_ShouldWork()
        {
            // Arrange - Test programmatic configuration for third-party services
            var configManager = new MethodCache.Core.Configuration.ConfigurationManager(null);
            var programmaticSource = new ProgrammaticConfigurationSource();
            
            // Configure third-party services programmatically
            programmaticSource.AddMethodConfiguration("IStripeClient", "GetCustomerAsync", new CacheMethodSettings
            {
                Duration = TimeSpan.FromHours(2),
                Tags = new List<string> { "stripe", "customer", "programmatic" },
                Version = 1
            });

            programmaticSource.AddMethodConfiguration("IWeatherApiClient", "GetForecastAsync", new CacheMethodSettings
            {
                Duration = TimeSpan.FromHours(1),
                Tags = new List<string> { "weather", "forecast" },
                Version = 1
            });

            configManager.AddSource(programmaticSource);

            // Act
            await configManager.LoadConfigurationAsync();

            // Assert
            var stripeCustomer = configManager.GetMethodConfiguration("IStripeClient", "GetCustomerAsync");
            Assert.NotNull(stripeCustomer);
            Assert.Equal(TimeSpan.FromHours(2), stripeCustomer.Duration);
            Assert.Contains("programmatic", stripeCustomer.Tags);

            var weatherForecast = configManager.GetMethodConfiguration("IWeatherApiClient", "GetForecastAsync");
            Assert.NotNull(weatherForecast);
            Assert.Equal(TimeSpan.FromHours(1), weatherForecast.Duration);
        }

        [Fact]
        public async Task ConfigurationPriority_RuntimeOverridesEverything()
        {
            // Arrange - Test the priority system: Runtime (40) > Programmatic (30) > JSON (20)
            var jsonConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Duration"] = "01:00:00", // JSON says 1 hour
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Tags:0"] = "json-config"
                })
                .Build();

            var configManager = new MethodCache.Core.Configuration.ConfigurationManager(null);
            
            // Add JSON source (priority 20)
            configManager.AddSource(new JsonConfigurationSource(jsonConfig));
            
            // Add Programmatic source (priority 30)
            var programmaticSource = new ProgrammaticConfigurationSource();
            programmaticSource.AddMethodConfiguration("IStripeClient", "GetCustomerAsync", new CacheMethodSettings
            {
                Duration = TimeSpan.FromHours(2), // Programmatic says 2 hours
                Tags = new List<string> { "programmatic-config" }
            });
            configManager.AddSource(programmaticSource);

            // Add Runtime source (priority 40 - highest)
            var runtimeSource = new TestRuntimeConfigurationSource();
            runtimeSource.AddMethod("IStripeClient", "GetCustomerAsync", new CacheMethodSettings
            {
                Duration = TimeSpan.FromMinutes(30), // Runtime says 30 minutes - should win!
                Tags = new List<string> { "runtime-override", "emergency-tuning" }
            });
            configManager.AddSource(runtimeSource);

            // Act
            await configManager.LoadConfigurationAsync();
            var finalConfig = configManager.GetMethodConfiguration("IStripeClient", "GetCustomerAsync");

            // Assert - Runtime configuration should override everything
            Assert.NotNull(finalConfig);
            Assert.Equal(TimeSpan.FromMinutes(30), finalConfig.Duration); // Runtime wins!
            Assert.Contains("runtime-override", finalConfig.Tags);
            Assert.Contains("emergency-tuning", finalConfig.Tags);
            Assert.DoesNotContain("json-config", finalConfig.Tags); // JSON was overridden
            Assert.DoesNotContain("programmatic-config", finalConfig.Tags); // Programmatic was overridden
        }

        [Fact]
        public async Task EmergencyDisable_ShouldOverrideAllConfiguration()
        {
            // Arrange - Simulate emergency disable scenario from CONFIGURATION_GUIDE.md
            var baseConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Duration"] = "02:00:00"
                })
                .Build();

            var configManager = new MethodCache.Core.Configuration.ConfigurationManager(null);
            configManager.AddSource(new JsonConfigurationSource(baseConfig));

            // Simulate emergency override via management interface
            var emergencySource = new TestRuntimeConfigurationSource();
            emergencySource.AddMethod("IStripeClient", "GetCustomerAsync", new CacheMethodSettings
            {
                Duration = TimeSpan.FromSeconds(1), // Minimal cache duration = effectively disabled
                Tags = new List<string> { "emergency-disabled", "incident-response" }
            });
            configManager.AddSource(emergencySource);

            // Act
            await configManager.LoadConfigurationAsync();
            var config = configManager.GetMethodConfiguration("IStripeClient", "GetCustomerAsync");

            // Assert - Service should be effectively disabled
            Assert.NotNull(config);
            Assert.Equal(TimeSpan.FromSeconds(1), config.Duration); // Minimal cache = effectively disabled
            Assert.Contains("emergency-disabled", config.Tags);
            Assert.Contains("incident-response", config.Tags);
        }

        [Fact]
        public async Task ThirdPartyTagging_EnablesBulkInvalidation()
        {
            // Arrange - Test tag-based invalidation for third-party services
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    // All Stripe operations get 'stripe' tag for bulk invalidation
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Tags:0"] = "stripe",
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Tags:1"] = "customer",
                    ["MethodCache:Services:IStripeClient.GetCustomerAsync:Tags:2"] = "external-api",
                    ["MethodCache:Services:IStripeClient.GetPaymentIntentAsync:Tags:0"] = "stripe", 
                    ["MethodCache:Services:IStripeClient.GetPaymentIntentAsync:Tags:1"] = "payment",
                    ["MethodCache:Services:IStripeClient.GetPaymentIntentAsync:Tags:2"] = "external-api",

                    // All weather operations get 'weather' tag
                    ["MethodCache:Services:IWeatherApiClient.GetCurrentWeatherAsync:Tags:0"] = "weather",
                    ["MethodCache:Services:IWeatherApiClient.GetCurrentWeatherAsync:Tags:1"] = "current",
                    ["MethodCache:Services:IWeatherApiClient.GetCurrentWeatherAsync:Tags:2"] = "external-api",
                    ["MethodCache:Services:IWeatherApiClient.GetForecastAsync:Tags:0"] = "weather",
                    ["MethodCache:Services:IWeatherApiClient.GetForecastAsync:Tags:1"] = "forecast",
                    ["MethodCache:Services:IWeatherApiClient.GetForecastAsync:Tags:2"] = "external-api"
                })
                .Build();

            var configManager = new MethodCache.Core.Configuration.ConfigurationManager(null);
            configManager.AddSource(new JsonConfigurationSource(configuration));

            // Act
            await configManager.LoadConfigurationAsync();

            // Assert - Verify tagging enables bulk operations
            var stripeCustomer = configManager.GetMethodConfiguration("IStripeClient", "GetCustomerAsync");
            Assert.NotNull(stripeCustomer);
            Assert.Contains("stripe", stripeCustomer.Tags);
            Assert.Contains("external-api", stripeCustomer.Tags);

            var stripePayment = configManager.GetMethodConfiguration("IStripeClient", "GetPaymentIntentAsync");
            Assert.NotNull(stripePayment);
            Assert.Contains("stripe", stripePayment.Tags);
            Assert.Contains("external-api", stripePayment.Tags);

            var weatherCurrent = configManager.GetMethodConfiguration("IWeatherApiClient", "GetCurrentWeatherAsync");
            Assert.NotNull(weatherCurrent);
            Assert.Contains("weather", weatherCurrent.Tags);
            Assert.Contains("external-api", weatherCurrent.Tags);

            var weatherForecast = configManager.GetMethodConfiguration("IWeatherApiClient", "GetForecastAsync");
            Assert.NotNull(weatherForecast);
            Assert.Contains("weather", weatherForecast.Tags);
            Assert.Contains("external-api", weatherForecast.Tags);

            // This configuration enables:
            // - Invalidate all Stripe data: InvalidateByTagsAsync(["stripe"])
            // - Invalidate all weather data: InvalidateByTagsAsync(["weather"])  
            // - Emergency invalidate all external APIs: InvalidateByTagsAsync(["external-api"])
        }

        #region Helper Classes

        /// <summary>
        /// Test implementation of runtime configuration source for testing priority system
        /// </summary>
        private class TestRuntimeConfigurationSource : MethodCache.Core.Configuration.Sources.IConfigurationSource
        {
            private readonly List<MethodCacheConfigEntry> _entries = new();
            
            public int Priority => 40; // Highest priority - runtime override
            public bool SupportsRuntimeUpdates => true; // Runtime sources support updates

            public void AddMethod(string serviceName, string methodName, CacheMethodSettings settings)
            {
                _entries.Add(new MethodCacheConfigEntry
                {
                    ServiceType = serviceName,
                    MethodName = methodName,
                    Settings = settings
                });
            }

            public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync()
            {
                return Task.FromResult<IEnumerable<MethodCacheConfigEntry>>(_entries);
            }
        }

        #endregion
    }
}
