using FluentAssertions;
using MethodCache.Core;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public class RedisCacheManagerIntegrationTests : RedisIntegrationTestBase
{
    [Fact]
    public async Task GetOrCreateAsync_ShouldCreateAndCacheValue_WhenNotExists()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { "testArg" };
        var settings = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);
        var keyGenerator = ServiceProvider.GetRequiredService<ICacheKeyGenerator>();
        var callCount = 0;
        
        // Act - First call should execute factory
        var result1 = await CacheManager.GetOrCreateAsync(
            methodName, 
            args, 
            () => { callCount++; return Task.FromResult("generated-value"); }, 
            settings, 
            keyGenerator);
            
        // Act - Second call should use cache
        var result2 = await CacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => { callCount++; return Task.FromResult("should-not-be-called"); },
            settings,
            keyGenerator);

        // Assert
        result1.Should().Be("generated-value");
        result2.Should().Be("generated-value");
        callCount.Should().Be(1); // Factory should only be called once
    }

    [Fact]
    public async Task GetOrCreateAsync_WithComplexObject_ShouldSerializeCorrectly()
    {
        // Arrange
        var methodName = "ComplexObjectMethod";
        var args = new object[] { 123 };
        var settings = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);
        var keyGenerator = ServiceProvider.GetRequiredService<ICacheKeyGenerator>();
        
        var complexObject = new TestComplexObject
        {
            Id = 123,
            Name = "Test Object",
            CreatedAt = DateTime.UtcNow,
            Tags = new[] { "tag1", "tag2", "tag3" },
            Metadata = new Dictionary<string, string>
            {
                { "key1", "value1" },
                { "key2", "value2" }
            }
        };
        
        // Act
        var result = await CacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult(complexObject),
            settings,
            keyGenerator);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(complexObject.Id);
        result.Name.Should().Be(complexObject.Name);
        result.Tags.Should().BeEquivalentTo(complexObject.Tags);
        result.Metadata.Should().BeEquivalentTo(complexObject.Metadata);
    }

    public class TestComplexObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, string> Metadata { get; set; } = new();
    }
}