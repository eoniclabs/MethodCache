using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Infrastructure.Abstractions;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public class SqlServerStorageProviderIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact]
    public async Task SetAsync_WithBasicValue_ShouldStoreAndRetrieve()
    {
        // Arrange
        var storageProvider = ServiceProvider.GetRequiredService<IStorageProvider>();
        var key = "test-key";
        var value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);

        // Act
        await storageProvider.SetAsync(key, value, expiration);
        var retrieved = await storageProvider.GetAsync<string>(key);

        // Assert
        retrieved.Should().Be(value);
    }

    [Fact]
    public async Task SetAsync_WithComplexObject_ShouldStoreAndRetrieve()
    {
        // Arrange
        var storageProvider = ServiceProvider.GetRequiredService<IStorageProvider>();
        var key = "test-complex";
        var value = new TestObject { Id = 123, Name = "Test", Items = new[] { "item1", "item2" } };
        var expiration = TimeSpan.FromMinutes(5);

        // Act
        await storageProvider.SetAsync(key, value, expiration);
        var retrieved = await storageProvider.GetAsync<TestObject>(key);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(value.Id);
        retrieved.Name.Should().Be(value.Name);
        retrieved.Items.Should().BeEquivalentTo(value.Items);
    }

    [Fact]
    public async Task SetAsync_WithTags_ShouldAllowTagBasedRemoval()
    {
        // Arrange
        var storageProvider = ServiceProvider.GetRequiredService<IStorageProvider>();
        var key1 = "test-tagged-1";
        var key2 = "test-tagged-2";
        var value1 = "value1";
        var value2 = "value2";
        var tag = "test-tag";
        var expiration = TimeSpan.FromMinutes(5);

        // Act
        await storageProvider.SetAsync(key1, value1, expiration, new[] { tag });
        await storageProvider.SetAsync(key2, value2, expiration, new[] { tag });

        // Verify both values are stored
        var retrieved1 = await storageProvider.GetAsync<string>(key1);
        var retrieved2 = await storageProvider.GetAsync<string>(key2);
        retrieved1.Should().Be(value1);
        retrieved2.Should().Be(value2);

        // Remove by tag
        await storageProvider.RemoveByTagAsync(tag);

        // Verify both values are removed
        var afterRemoval1 = await storageProvider.GetAsync<string>(key1);
        var afterRemoval2 = await storageProvider.GetAsync<string>(key2);
        afterRemoval1.Should().BeNull();
        afterRemoval2.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithNonExistentKey_ShouldReturnNull()
    {
        // Arrange
        var storageProvider = ServiceProvider.GetRequiredService<IStorageProvider>();
        var key = "non-existent-key";

        // Act
        var result = await storageProvider.GetAsync<string>(key);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_WithExistingKey_ShouldRemoveValue()
    {
        // Arrange
        var storageProvider = ServiceProvider.GetRequiredService<IStorageProvider>();
        var key = "test-remove";
        var value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);

        // Act
        await storageProvider.SetAsync(key, value, expiration);
        var beforeRemoval = await storageProvider.GetAsync<string>(key);
        await storageProvider.RemoveAsync(key);
        var afterRemoval = await storageProvider.GetAsync<string>(key);

        // Assert
        beforeRemoval.Should().Be(value);
        afterRemoval.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WithExpiration_ShouldExpireAfterTimespan()
    {
        // Arrange
        var storageProvider = ServiceProvider.GetRequiredService<IStorageProvider>();
        var key = "test-expiration";
        var value = "test-value";
        var expiration = TimeSpan.FromSeconds(1); // Very short for test

        // Act
        await storageProvider.SetAsync(key, value, expiration);
        var beforeExpiration = await storageProvider.GetAsync<string>(key);

        // Wait for expiration
        await Task.Delay(TimeSpan.FromSeconds(2));

        var afterExpiration = await storageProvider.GetAsync<string>(key);

        // Assert
        beforeExpiration.Should().Be(value);
        afterExpiration.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WithMultipleTags_ShouldAllowSelectiveTagRemoval()
    {
        // Arrange
        var storageProvider = ServiceProvider.GetRequiredService<IStorageProvider>();
        var key1 = "test-multi-tag-1";
        var key2 = "test-multi-tag-2";
        var key3 = "test-multi-tag-3";
        var value1 = "value1";
        var value2 = "value2";
        var value3 = "value3";
        var tagA = "tag-a";
        var tagB = "tag-b";
        var expiration = TimeSpan.FromMinutes(5);

        // Act
        await storageProvider.SetAsync(key1, value1, expiration, new[] { tagA });
        await storageProvider.SetAsync(key2, value2, expiration, new[] { tagB });
        await storageProvider.SetAsync(key3, value3, expiration, new[] { tagA, tagB });

        // Remove by tag A only
        await storageProvider.RemoveByTagAsync(tagA);

        // Verify selective removal
        var afterRemovalA1 = await storageProvider.GetAsync<string>(key1);
        var afterRemovalA2 = await storageProvider.GetAsync<string>(key2);
        var afterRemovalA3 = await storageProvider.GetAsync<string>(key3);

        // Assert
        afterRemovalA1.Should().BeNull(); // Had tag A
        afterRemovalA2.Should().Be(value2); // Had only tag B
        afterRemovalA3.Should().BeNull(); // Had both tags A and B
    }

    private class TestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string[] Items { get; set; } = Array.Empty<string>();
    }
}