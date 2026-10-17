using System;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Extensibility;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MethodCache.Core.Tests.Core
{
    public class ServiceRegistrationTests
    {
        // Test interface with cache attributes
        public interface ITestCacheService
        {
            [Cache]
            Task<string> GetDataAsync(string key);

            [CacheInvalidate(Tags = new[] { "test" })]
            Task InvalidateAsync();
        }

        // Test implementation
        public class TestCacheService : ITestCacheService
        {
            public virtual async Task<string> GetDataAsync(string key)
            {
                await Task.Delay(10); // Simulate some work
                return $"Data-{key}-{DateTime.Now.Ticks}";
            }

            public virtual async Task InvalidateAsync()
            {
                await Task.CompletedTask;
            }
        }

        // Interface without cache attributes (should be ignored)
        public interface INonCacheService
        {
            string GetValue();
        }

        public class NonCacheService : INonCacheService
        {
            public string GetValue() => "value";
        }

        [Fact]
        public void AddMethodCache_WithAssemblyParameter_RegistersCoreServices()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddMethodCache(b => b.FromAssembly(Assembly.GetExecutingAssembly()));

            // Assert
            var serviceProvider = services.BuildServiceProvider();

            // Verify core services are registered
            Assert.NotNull(serviceProvider.GetService<IPolicyRegistry>());
            Assert.NotNull(serviceProvider.GetService<IRuntimeCacheConfigurator>());
            Assert.NotNull(serviceProvider.GetService<ICacheManager>());
            Assert.NotNull(serviceProvider.GetService<ICacheKeyGenerator>());
            Assert.NotNull(serviceProvider.GetService<System.Diagnostics.Metrics.IMeterFactory>());
        }

        [Fact]
        public void AddMethodCache_ExposesTheCacheManagerAsMemoryCache()
        {
            var services = new ServiceCollection();
            services.AddMethodCache(b => b.FromAssembly(Assembly.GetExecutingAssembly()));

            using var serviceProvider = services.BuildServiceProvider();

            var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
            var memoryCache = serviceProvider.GetRequiredService<IMemoryCache>();
            Assert.Same(cacheManager, memoryCache);
        }

        [Fact]
        public void AddMethodCache_KeepsACustomCacheManager()
        {
            var services = new ServiceCollection();
            var custom = NSubstitute.Substitute.For<ICacheManager>();
            services.AddSingleton(custom);
            services.AddMethodCache(b => b.FromAssembly(Assembly.GetExecutingAssembly()));

            using var serviceProvider = services.BuildServiceProvider();

            Assert.Same(custom, serviceProvider.GetRequiredService<ICacheManager>());
            Assert.Null(serviceProvider.GetService<IMemoryCache>());
        }
    }
}
