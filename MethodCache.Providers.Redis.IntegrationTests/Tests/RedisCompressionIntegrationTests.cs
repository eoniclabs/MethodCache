using FluentAssertions;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public class RedisCompressionIntegrationTests : RedisIntegrationTestBase
{
    [Fact]
    public async Task GetOrCreateAsync_WithGzipCompression_ShouldCompressLargeData()
    {
        // Arrange - Create service provider with Gzip compression
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.Compression = RedisCompressionType.Gzip;
            options.CompressionThreshold = 100; // Low threshold for testing
            options.KeyPrefix = "gzip-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Create large data that will be compressed
        var largeData = new TestLargeObject
        {
            Id = 12345,
            Name = "Large Test Object",
            Description = new string('A', 2000), // 2KB of data
            Tags = Enumerable.Range(1, 100).Select(i => $"tag-{i}").ToArray(),
            Metadata = Enumerable.Range(1, 50).ToDictionary(i => $"key-{i}", i => $"value-{i}-{new string('X', 50)}")
        };

        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };

        // Act
        var result = await cacheManager.GetOrCreateAsync(
            "GetLargeData",
            new object[] { largeData.Id },
            async () => largeData,
            settings,
            keyGenerator,
            false);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(largeData.Id);
        result.Name.Should().Be(largeData.Name);
        result.Description.Should().Be(largeData.Description);
        result.Tags.Should().BeEquivalentTo(largeData.Tags);
        result.Metadata.Should().BeEquivalentTo(largeData.Metadata);

        // Verify cache hit on second call
        var callCount = 0;
        var cachedResult = await cacheManager.GetOrCreateAsync(
            "GetLargeData",
            new object[] { largeData.Id },
            async () => { callCount++; return new TestLargeObject(); },
            settings,
            keyGenerator,
            false);

        cachedResult.Should().BeEquivalentTo(result);
        callCount.Should().Be(0); // Should not call factory

        serviceProvider.Dispose();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithBrotliCompression_ShouldCompressLargeData()
    {
        // Arrange - Create service provider with Brotli compression
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.Compression = RedisCompressionType.Brotli;
            options.CompressionThreshold = 100;
            options.KeyPrefix = "brotli-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Create large data
        var largeData = new TestLargeObject
        {
            Id = 67890,
            Name = "Brotli Test Object",
            Description = new string('B', 3000), // 3KB of data
            Tags = Enumerable.Range(1, 150).Select(i => $"brotli-tag-{i}").ToArray(),
            Metadata = Enumerable.Range(1, 75).ToDictionary(i => $"brotli-key-{i}", i => $"brotli-value-{i}-{new string('Y', 60)}")
        };

        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };

        // Act
        var result = await cacheManager.GetOrCreateAsync(
            "GetBrotliData",
            new object[] { largeData.Id },
            async () => largeData,
            settings,
            keyGenerator,
            false);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(largeData.Id);
        result.Name.Should().Be(largeData.Name);
        result.Description.Should().Be(largeData.Description);
        result.Tags.Should().BeEquivalentTo(largeData.Tags);
        result.Metadata.Should().BeEquivalentTo(largeData.Metadata);

        serviceProvider.Dispose();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithNoCompression_ShouldWorkNormally()
    {
        // Arrange - Use base test class setup (no compression)
        var keyGenerator = ServiceProvider.GetRequiredService<ICacheKeyGenerator>();
        var smallData = new TestSmallObject
        {
            Id = 1,
            Name = "Small Object",
            Value = "This is small data that won't be compressed"
        };

        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };

        // Act
        var result = await CacheManager.GetOrCreateAsync(
            "GetSmallData",
            new object[] { smallData.Id },
            async () => smallData,
            settings,
            keyGenerator,
            false);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(smallData.Id);
        result.Name.Should().Be(smallData.Name);
        result.Value.Should().Be(smallData.Value);
    }

    [Fact]
    public async Task GetOrCreateAsync_CompressionThreshold_ShouldOnlyCompressBeyondThreshold()
    {
        // Arrange - Set high compression threshold
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.Compression = RedisCompressionType.Gzip;
            options.CompressionThreshold = 5000; // High threshold
            options.KeyPrefix = "threshold-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Create medium-sized data (below threshold)
        var mediumData = new TestLargeObject
        {
            Id = 999,
            Name = "Medium Object",
            Description = new string('M', 1000), // 1KB - below 5KB threshold
            Tags = new[] { "medium", "test" },
            Metadata = new Dictionary<string, string> { { "size", "medium" } }
        };

        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };

        // Act
        var result = await cacheManager.GetOrCreateAsync(
            "GetMediumData",
            new object[] { mediumData.Id },
            async () => mediumData,
            settings,
            keyGenerator,
            false);

        // Assert - Should work normally even without compression
        result.Should().NotBeNull();
        result.Description.Length.Should().Be(1000);

        serviceProvider.Dispose();
    }

    public class TestLargeObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string[] Tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    public class TestSmallObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}