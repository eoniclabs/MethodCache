using FluentAssertions;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public class RedisTagManagerIntegrationTests : RedisIntegrationTestBase
{
    [Fact]
    public async Task InvalidateByTagsAsync_ShouldRemoveTaggedEntries()
    {
        // Arrange
        var tagManager = ServiceProvider.GetRequiredService<IRedisTagManager>();
        
        var keyGenerator = ServiceProvider.GetRequiredService<ICacheKeyGenerator>();
        
        var settings1 = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:123", "profile" }
        };
        var settings2 = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:456", "profile" }
        };
        var settings3 = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:123", "settings" }
        };

        await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 123 }, async () => "Profile Data 123", settings1, keyGenerator, false);
        await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 456 }, async () => "Profile Data 456", settings2, keyGenerator, false);
        await CacheManager.GetOrCreateAsync("GetSettings", new object[] { 123 }, async () => "Settings Data 123", settings3, keyGenerator, false);

        // Act - Invalidate by "user:123" tag
        await CacheManager.InvalidateByTagsAsync("user:123");

        // Assert - Check by calling GetOrCreate again, should re-execute factory if invalidated
        var callCount = 0;
        var profile123 = await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 123 }, async () => { callCount++; return "New Profile 123"; }, settings1, keyGenerator, false);
        var profile456 = await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 456 }, async () => { callCount++; return "New Profile 456"; }, settings2, keyGenerator, false);
        var settings123 = await CacheManager.GetOrCreateAsync("GetSettings", new object[] { 123 }, async () => { callCount++; return "New Settings 123"; }, settings3, keyGenerator, false);

        profile123.Should().Be("New Profile 123"); // Should be re-created (invalidated)
        profile456.Should().Be("Profile Data 456"); // Should remain from cache
        settings123.Should().Be("New Settings 123"); // Should be re-created (invalidated)
        callCount.Should().Be(2); // Two items were invalidated and re-created
    }

    [Fact]
    public async Task InvalidateByTagsAsync_WithMultipleTags_ShouldInvalidateAllMatchingEntries()
    {
        // Arrange
        var tagManager = ServiceProvider.GetRequiredService<IRedisTagManager>();
        
        var keyGenerator = ServiceProvider.GetRequiredService<ICacheKeyGenerator>();
        
        var settings1 = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "region:us-east", "type:user" }
        };
        var settings2 = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "region:us-west", "type:user" }
        };
        var settings3 = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "region:eu-west", "type:admin" }
        };

        await CacheManager.GetOrCreateAsync("GetData", new object[] { 1 }, async () => "value1", settings1, keyGenerator, false);
        await CacheManager.GetOrCreateAsync("GetData", new object[] { 2 }, async () => "value2", settings2, keyGenerator, false);
        await CacheManager.GetOrCreateAsync("GetData", new object[] { 3 }, async () => "value3", settings3, keyGenerator, false);

        // Act - Invalidate by multiple tags
        await CacheManager.InvalidateByTagsAsync("type:user", "region:eu-west");

        // Assert
        var callCount = 0;
        var value1 = await CacheManager.GetOrCreateAsync("GetData", new object[] { 1 }, async () => { callCount++; return "new1"; }, settings1, keyGenerator, false);
        var value2 = await CacheManager.GetOrCreateAsync("GetData", new object[] { 2 }, async () => { callCount++; return "new2"; }, settings2, keyGenerator, false);
        var value3 = await CacheManager.GetOrCreateAsync("GetData", new object[] { 3 }, async () => { callCount++; return "new3"; }, settings3, keyGenerator, false);

        value1.Should().Be("new1"); // Matches "type:user" - invalidated
        value2.Should().Be("new2"); // Matches "type:user" - invalidated  
        value3.Should().Be("new3"); // Matches "region:eu-west" - invalidated
        callCount.Should().Be(3); // All entries were invalidated
    }

    [Fact]
    public async Task InvalidateByTagsAsync_WithNonExistentTag_ShouldNotAffectOtherEntries()
    {
        // Arrange
        var tagManager = ServiceProvider.GetRequiredService<IRedisTagManager>();
        
        var keyGenerator = ServiceProvider.GetRequiredService<ICacheKeyGenerator>();
        var settings = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "existing-tag" }
        };

        await CacheManager.GetOrCreateAsync("TestMethod", new object[] { "test" }, async () => "test-value", settings, keyGenerator, false);

        // Act - Try to invalidate with non-existent tag
        await CacheManager.InvalidateByTagsAsync("non-existent-tag");

        // Assert
        var callCount = 0;
        var value = await CacheManager.GetOrCreateAsync("TestMethod", new object[] { "test" }, async () => { callCount++; return "new-value"; }, settings, keyGenerator, false);
        value.Should().Be("test-value"); // Should remain unchanged (not invalidated)
        callCount.Should().Be(0); // Factory should not be called since entry wasn't invalidated
    }
}