using System;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Abstractions.Registry;
using MethodCache.Core;
using MethodCache.Core.Configuration.Runtime;
using MethodCache.Core.Runtime;
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
            services.AddMethodCache(null, Assembly.GetExecutingAssembly());

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            
            // Verify core services are registered
            Assert.NotNull(serviceProvider.GetService<IPolicyRegistry>());
            Assert.NotNull(serviceProvider.GetService<IRuntimeCacheConfigurator>());
            Assert.NotNull(serviceProvider.GetService<ICacheManager>());
            Assert.NotNull(serviceProvider.GetService<ICacheKeyGenerator>());
            Assert.NotNull(serviceProvider.GetService<ICacheMetricsProvider>());
        }

        [Fact]
        public void AddMethodCache_WithOptions_RegistersCoreServices()
        {
            // Arrange
            var services = new ServiceCollection();
            var options = new MethodCacheRegistrationOptions
            {
                Assemblies = new[] { Assembly.GetExecutingAssembly() }
            };

            // Act
            services.AddMethodCache(null, options);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            
            // Verify core services are registered
            Assert.NotNull(serviceProvider.GetService<IPolicyRegistry>());
            Assert.NotNull(serviceProvider.GetService<IRuntimeCacheConfigurator>());
            Assert.NotNull(serviceProvider.GetService<ICacheManager>());
            Assert.NotNull(serviceProvider.GetService<ICacheKeyGenerator>());
            Assert.NotNull(serviceProvider.GetService<ICacheMetricsProvider>());
        }

        [Fact]
        public void AddMethodCacheServices_RegistersConcreteImplementations()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddMethodCache(); // Add core services first

            // Act - This will attempt to register services, but may not find the generated decorators
            // That's okay for this test - we're testing the registration logic
            services.AddMethodCacheServices(new[] { Assembly.GetExecutingAssembly() });

            // Assert
            var serviceProvider = services.BuildServiceProvider();

            // Should be able to resolve the concrete implementation
            // (The cached interface resolution depends on the source generator)
            Assert.NotNull(serviceProvider.GetService<TestCacheService>());
        }

        [Fact]
        public void MethodCacheRegistrationOptions_DefaultValues_AreCorrect()
        {
            // Arrange & Act
            var options = new MethodCacheRegistrationOptions();

            // Assert
            Assert.Equal(ServiceLifetime.Singleton, options.DefaultServiceLifetime);
            Assert.True(options.RegisterConcreteImplementations);
            Assert.False(options.ThrowOnMissingImplementation);
            Assert.False(options.ScanReferencedAssemblies);
            Assert.Null(options.InterfaceFilter);
            Assert.Null(options.ImplementationFilter);
            Assert.Null(options.ServiceLifetimeResolver);
        }

        [Fact]
        public void MethodCacheRegistrationOptions_ForAssemblies_SetsAssemblies()
        {
            // Arrange
            var assembly1 = Assembly.GetExecutingAssembly();
            var assembly2 = typeof(MethodCacheServiceCollectionExtensions).Assembly;

            // Act
            var options = MethodCacheRegistrationOptions.ForAssemblies(assembly1, assembly2);

            // Assert
            Assert.NotNull(options.Assemblies);
            Assert.Equal(2, options.Assemblies.Length);
            Assert.Contains(assembly1, options.Assemblies);
            Assert.Contains(assembly2, options.Assemblies);
        }

        [Fact]
        public void MethodCacheRegistrationOptions_ForAssemblyContaining_SetsCorrectAssembly()
        {
            // Act
            var options = MethodCacheRegistrationOptions.ForAssemblyContaining<ServiceRegistrationTests>();

            // Assert
            Assert.NotNull(options.Assemblies);
            Assert.Single(options.Assemblies);
            Assert.Equal(Assembly.GetExecutingAssembly(), options.Assemblies[0]);
        }

        [Fact]
        public void MethodCacheRegistrationOptions_Default_SetsCallingAssembly()
        {
            // Act
            var options = MethodCacheRegistrationOptions.Default();

            // Assert
            Assert.NotNull(options.Assemblies);
            Assert.Single(options.Assemblies);
            // Note: In unit tests, the calling assembly might be the test runner
            // so we just verify that an assembly is set
            Assert.NotNull(options.Assemblies[0]);
        }

        [Fact]
        public void AddMethodCacheServices_WithInterfaceFilter_FiltersCorrectly()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddMethodCache(); // Add core services first

            var options = new MethodCacheRegistrationOptions
            {
                Assemblies = new[] { Assembly.GetExecutingAssembly() },
                InterfaceFilter = type => type.Name.StartsWith("ITest")
            };

            // Act
            services.AddMethodCacheServices(options);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            
            // Should register TestCacheService since ITestCacheService starts with "ITest"
            Assert.NotNull(serviceProvider.GetService<TestCacheService>());
        }

        [Fact]
        public void AddMethodCacheServices_WithImplementationFilter_FiltersCorrectly()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddMethodCache(); // Add core services first

            var options = new MethodCacheRegistrationOptions
            {
                Assemblies = new[] { Assembly.GetExecutingAssembly() },
                ImplementationFilter = type => type.Name.Contains("Cache")
            };

            // Act
            services.AddMethodCacheServices(options);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            
            // Should register TestCacheService since it contains "Cache"
            Assert.NotNull(serviceProvider.GetService<TestCacheService>());
        }
    }
}
