using FluentAssertions;
using MethodCache.Core;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime;
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

        var settings1 = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:123", "profile" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);
        var settings2 = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:456", "profile" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);
        var settings3 = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:123", "settings" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);

        await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 123 }, () => Task.FromResult("Profile Data 123"), settings1, keyGenerator);
        await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 456 }, () => Task.FromResult("Profile Data 456"), settings2, keyGenerator);
        await CacheManager.GetOrCreateAsync("GetSettings", new object[] { 123 }, () => Task.FromResult("Settings Data 123"), settings3, keyGenerator);

        // Act - Invalidate by "user:123" tag
        await CacheManager.InvalidateByTagsAsync("user:123");

        // Assert - Check by calling GetOrCreate again, should re-execute factory if invalidated
        var callCount = 0;
        var profile123 = await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 123 }, () => { callCount++; return Task.FromResult("New Profile 123"); }, settings1, keyGenerator);
        var profile456 = await CacheManager.GetOrCreateAsync("GetProfile", new object[] { 456 }, () => { callCount++; return Task.FromResult("New Profile 456"); }, settings2, keyGenerator);
        var settings123 = await CacheManager.GetOrCreateAsync("GetSettings", new object[] { 123 }, () => { callCount++; return Task.FromResult("New Settings 123"); }, settings3, keyGenerator);

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

        var settings1 = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "region:us-east", "type:user" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);
        var settings2 = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "region:us-west", "type:user" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);
        var settings3 = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "region:eu-west", "type:admin" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);

        await CacheManager.GetOrCreateAsync("GetData", new object[] { 1 }, () => Task.FromResult("value1"), settings1, keyGenerator);
        await CacheManager.GetOrCreateAsync("GetData", new object[] { 2 }, () => Task.FromResult("value2"), settings2, keyGenerator);
        await CacheManager.GetOrCreateAsync("GetData", new object[] { 3 }, () => Task.FromResult("value3"), settings3, keyGenerator);

        // Act - Invalidate by multiple tags
        await CacheManager.InvalidateByTagsAsync("type:user", "region:eu-west");

        // Assert
        var callCount = 0;
        var value1 = await CacheManager.GetOrCreateAsync("GetData", new object[] { 1 }, () => { callCount++; return Task.FromResult("new1"); }, settings1, keyGenerator);
        var value2 = await CacheManager.GetOrCreateAsync("GetData", new object[] { 2 }, () => { callCount++; return Task.FromResult("new2"); }, settings2, keyGenerator);
        var value3 = await CacheManager.GetOrCreateAsync("GetData", new object[] { 3 }, () => { callCount++; return Task.FromResult("new3"); }, settings3, keyGenerator);

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
        var settings = CacheRuntimeDescriptor.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "existing-tag" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);

        await CacheManager.GetOrCreateAsync("TestMethod", new object[] { "test" }, () => Task.FromResult("test-value"), settings, keyGenerator);

        // Act - Try to invalidate with non-existent tag
        await CacheManager.InvalidateByTagsAsync("non-existent-tag");

        // Assert
        var callCount = 0;
        var value = await CacheManager.GetOrCreateAsync("TestMethod", new object[] { "test" }, () => { callCount++; return Task.FromResult("new-value"); }, settings, keyGenerator);
        value.Should().Be("test-value"); // Should remain unchanged (not invalidated)
        callCount.Should().Be(0); // Factory should not be called since entry wasn't invalidated
    }
}