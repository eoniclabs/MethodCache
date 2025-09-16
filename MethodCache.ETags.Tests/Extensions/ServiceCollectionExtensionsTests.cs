using FluentAssertions;
using MethodCache.ETags.Abstractions;
using MethodCache.ETags.Extensions;
using MethodCache.ETags.Implementation;
using MethodCache.ETags.Middleware;
using MethodCache.ETags.Models;
using MethodCache.HybridCache.Abstractions;
using MethodCache.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.ETags.Tests.Extensions
{
    public class ServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddETagSupport_WithHybridCacheRegistered_ShouldRegisterETagServices()
        {
            // Arrange
            var services = new ServiceCollection();
            var mockHybridCache = new Mock<IHybridCacheManager>();
            services.AddSingleton(mockHybridCache.Object);

            // Act
            services.AddETagSupport();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            
            serviceProvider.GetService<IETagCacheManager>().Should().NotBeNull();
            serviceProvider.GetService<IETagCacheManager>().Should().BeOfType<ETagHybridCacheManager>();
        }

        [Fact]
        public void AddETagSupport_WithoutHybridCache_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() => services.AddETagSupport());
            exception.Message.Should().Contain("IHybridCacheManager must be registered before adding ETag support");
        }

        [Fact]
        public void AddETagSupport_WithConfiguration_ShouldConfigureOptions()
        {
            // Arrange
            var services = new ServiceCollection();
            var mockHybridCache = new Mock<IHybridCacheManager>();
            services.AddSingleton(mockHybridCache.Object);

            var expectedExpiration = TimeSpan.FromMinutes(45);
            var expectedContentTypes = new[] { "application/json", "text/xml" };

            // Act
            services.AddETagSupport(options =>
            {
                options.DefaultExpiration = expectedExpiration;
                options.CacheableContentTypes = expectedContentTypes;
            });

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var optionsMonitor = serviceProvider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ETagMiddlewareOptions>>();
            
            optionsMonitor.Should().NotBeNull();
            optionsMonitor!.CurrentValue.DefaultExpiration.Should().Be(expectedExpiration);
            optionsMonitor.CurrentValue.CacheableContentTypes.Should().BeEquivalentTo(expectedContentTypes);
        }

        [Fact]
        public void AddETagSupport_WithCustomETagCacheManager_ShouldRegisterCustomManager()
        {
            // Arrange
            var services = new ServiceCollection();
            var customManager = new Mock<IETagCacheManager>();

            // Act
            services.AddETagSupport<CustomETagCacheManager>();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IETagCacheManager>().Should().BeOfType<CustomETagCacheManager>();
        }

        [Fact]
        public void AddETagBackplane_ShouldRegisterBackplane()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddETagBackplane<CustomETagBackplane>();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IETagCacheBackplane>().Should().BeOfType<CustomETagBackplane>();
        }

        [Fact]
        public void ConfigureETagMiddleware_ShouldConfigureOptions()
        {
            // Arrange
            var services = new ServiceCollection();
            var expectedMaxAge = TimeSpan.FromHours(2);

            // Act
            services.ConfigureETagMiddleware(options =>
            {
                options.DefaultCacheMaxAge = expectedMaxAge;
                options.AddCacheControlHeader = true;
            });

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var optionsMonitor = serviceProvider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ETagMiddlewareOptions>>();
            
            optionsMonitor.Should().NotBeNull();
            optionsMonitor!.CurrentValue.DefaultCacheMaxAge.Should().Be(expectedMaxAge);
            optionsMonitor.CurrentValue.AddCacheControlHeader.Should().BeTrue();
        }

        [Fact]
        public void ETagBuilder_WithMiddlewareOptions_ShouldConfigureOptions()
        {
            // Arrange
            var services = new ServiceCollection();
            var expectedExpiration = TimeSpan.FromMinutes(20);

            // Act
            services.AddETagBuilder()
                .WithMiddlewareOptions(options =>
                {
                    options.DefaultExpiration = expectedExpiration;
                    options.EnableDebugLogging = true;
                })
                .Build();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var optionsMonitor = serviceProvider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ETagMiddlewareOptions>>();
            
            optionsMonitor.Should().NotBeNull();
            optionsMonitor!.CurrentValue.DefaultExpiration.Should().Be(expectedExpiration);
            optionsMonitor.CurrentValue.EnableDebugLogging.Should().BeTrue();
        }

        [Fact]
        public void ETagBuilder_WithBackplane_ShouldRegisterBackplane()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddETagBuilder()
                .WithBackplane<CustomETagBackplane>()
                .Build();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IETagCacheBackplane>().Should().BeOfType<CustomETagBackplane>();
        }

        [Fact]
        public void ETagBuilder_WithCustomCacheManager_ShouldRegisterCustomManager()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddETagBuilder()
                .WithETagCacheManager<CustomETagCacheManager>()
                .Build();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IETagCacheManager>().Should().BeOfType<CustomETagCacheManager>();
        }

        [Fact]
        public void AddETagSupportBuilder_WithHybridCache_ShouldRegisterETagSupportAndReturnBuilder()
        {
            // Arrange
            var services = new ServiceCollection();
            var mockHybridCache = new Mock<IHybridCacheManager>();
            services.AddSingleton(mockHybridCache.Object);

            // Act
            var builder = services.AddETagSupportBuilder()
                .WithMiddlewareOptions(options =>
                {
                    options.DefaultExpiration = TimeSpan.FromMinutes(15);
                });

            // Assert
            builder.Should().NotBeNull();
            builder.Should().BeOfType<ETagBuilder>();

            var serviceProvider = builder.Build().BuildServiceProvider();
            serviceProvider.GetService<IETagCacheManager>().Should().NotBeNull();
            
            var optionsMonitor = serviceProvider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ETagMiddlewareOptions>>();
            optionsMonitor!.CurrentValue.DefaultExpiration.Should().Be(TimeSpan.FromMinutes(15));
        }

        [Fact]
        public void ETagBuilder_ChainedConfiguration_ShouldApplyAllSettings()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddETagBuilder()
                .WithMiddlewareOptions(options =>
                {
                    options.DefaultExpiration = TimeSpan.FromMinutes(30);
                    options.AddCacheControlHeader = true;
                })
                .WithBackplane<CustomETagBackplane>()
                .WithETagCacheManager<CustomETagCacheManager>()
                .Build();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            
            // Verify cache manager
            serviceProvider.GetService<IETagCacheManager>().Should().BeOfType<CustomETagCacheManager>();
            
            // Verify backplane
            serviceProvider.GetService<IETagCacheBackplane>().Should().BeOfType<CustomETagBackplane>();
            
            // Verify options
            var optionsMonitor = serviceProvider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ETagMiddlewareOptions>>();
            optionsMonitor.Should().NotBeNull();
            optionsMonitor!.CurrentValue.DefaultExpiration.Should().Be(TimeSpan.FromMinutes(30));
            optionsMonitor.CurrentValue.AddCacheControlHeader.Should().BeTrue();
        }

        [Fact]
        public void AddETagSupport_TwiceWithSameType_ShouldNotThrow()
        {
            // Arrange
            var services = new ServiceCollection();
            var mockHybridCache = new Mock<IHybridCacheManager>();
            services.AddSingleton(mockHybridCache.Object);

            // Act & Assert
            services.AddETagSupport(); // First registration
            services.AddETagSupport(); // Second registration - should not throw

            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IETagCacheManager>().Should().NotBeNull();
        }
    }

    // Test helper classes
    public class CustomETagCacheManager : IETagCacheManager
    {
        public Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(string key, Func<Task<ETagCacheEntry<T>>> factory, string? ifNoneMatch = null, CacheMethodSettings? settings = null)
        {
            throw new NotImplementedException();
        }

        public Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(string key, Func<string?, Task<ETagCacheEntry<T>>> factory, string? ifNoneMatch = null, CacheMethodSettings? settings = null)
        {
            throw new NotImplementedException();
        }

        public Task InvalidateETagAsync(string key)
        {
            throw new NotImplementedException();
        }

        public Task InvalidateETagsAsync(params string[] keys)
        {
            throw new NotImplementedException();
        }

        public Task<string?> GetETagAsync(string key)
        {
            throw new NotImplementedException();
        }

        public Task<bool> IsETagValidAsync(string key, string etag)
        {
            throw new NotImplementedException();
        }
    }

    public class CustomETagBackplane : IETagCacheBackplane
    {
        public event EventHandler<ETagInvalidationEventArgs>? ETagInvalidationReceived;
        public event EventHandler<CacheInvalidationEventArgs>? InvalidationReceived;

        public bool IsConnected => true;

        public Task PublishETagInvalidationAsync(string key, string? newETag = null)
        {
            return Task.CompletedTask;
        }

        public Task PublishETagInvalidationBatchAsync(IEnumerable<KeyValuePair<string, string?>> invalidations)
        {
            return Task.CompletedTask;
        }

        public Task PublishInvalidationAsync(params string[] keys)
        {
            return Task.CompletedTask;
        }

        public Task PublishKeyInvalidationAsync(params string[] keys)
        {
            return Task.CompletedTask;
        }

        public Task StartListeningAsync()
        {
            return Task.CompletedTask;
        }

        public Task StopListeningAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // Clean up resources
        }
    }
}