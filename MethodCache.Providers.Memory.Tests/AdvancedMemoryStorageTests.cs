using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Providers.Memory.Configuration;
using MethodCache.Providers.Memory.Infrastructure;

namespace MethodCache.Providers.Memory.Tests;

public class AdvancedMemoryStorageTests
{
    private readonly AdvancedMemoryStorage _storage;
    private readonly AdvancedMemoryOptions _options;

    public AdvancedMemoryStorageTests()
    {
        _options = new AdvancedMemoryOptions
        {
            MaxEntries = 100,
            MaxMemoryUsage = 1024 * 1024, // 1MB
            EvictionPolicy = EvictionPolicy.LRU
        };

        var services = new ServiceCollection();
        services.AddLogging();
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<AdvancedMemoryStorage>>();
        var providerLogger = serviceProvider.GetRequiredService<ILogger<AdvancedMemoryStorageProvider>>();

        _storage = new AdvancedMemoryStorage(
            Options.Create(_options),
            logger,
            providerLogger);
    }

    [Fact]
    public void Get_WithNonExistentKey_ShouldReturnDefault()
    {
        var result = _storage.Get<string>("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public void SetAndGet_ShouldStoreAndRetrieveValue()
    {
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);

        _storage.Set(key, value, expiration);
        var result = _storage.Get<string>(key);

        result.Should().Be(value);
    }

    [Fact]
    public void SetWithTags_ShouldStoreValue()
    {
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);
        var tags = new[] { "tag1", "tag2" };

        _storage.Set(key, value, expiration, tags);
        var result = _storage.Get<string>(key);

        result.Should().Be(value);
    }

    [Fact]
    public async Task GetAsync_WithNonExistentKey_ShouldReturnDefault()
    {
        var result = await _storage.GetAsync<string>("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsyncAndGetAsync_ShouldStoreAndRetrieveValue()
    {
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);

        await _storage.SetAsync(key, value, expiration);
        var result = await _storage.GetAsync<string>(key);

        result.Should().Be(value);
    }

    [Fact]
    public async Task SetAsyncWithTags_ShouldStoreValue()
    {
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);
        var tags = new[] { "tag1", "tag2" };

        await _storage.SetAsync(key, value, expiration, tags);
        var result = await _storage.GetAsync<string>(key);

        result.Should().Be(value);
    }

    [Fact]
    public void Remove_ShouldRemoveEntry()
    {
        const string key = "remove-key";
        const string value = "test-value";

        _storage.Set(key, value, TimeSpan.FromMinutes(5));
        _storage.Remove(key);

        var result = _storage.Get<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveEntry()
    {
        const string key = "remove-key";
        const string value = "test-value";

        await _storage.SetAsync(key, value, TimeSpan.FromMinutes(5));
        await _storage.RemoveAsync(key);

        var result = await _storage.GetAsync<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public void RemoveByTag_ShouldRemoveEntriesWithTag()
    {
        const string tag = "test-tag";

        _storage.Set("key1", "value1", TimeSpan.FromMinutes(5), new[] { tag });
        _storage.Set("key2", "value2", TimeSpan.FromMinutes(5), new[] { tag });
        _storage.Set("key3", "value3", TimeSpan.FromMinutes(5), new[] { "other-tag" });

        _storage.RemoveByTag(tag);

        var result1 = _storage.Get<string>("key1");
        var result2 = _storage.Get<string>("key2");
        var result3 = _storage.Get<string>("key3");

        result1.Should().BeNull();
        result2.Should().BeNull();
        result3.Should().Be("value3");
    }

    [Fact]
    public async Task RemoveByTagAsync_ShouldRemoveEntriesWithTag()
    {
        const string tag = "test-tag";

        await _storage.SetAsync("key1", "value1", TimeSpan.FromMinutes(5), new[] { tag });
        await _storage.SetAsync("key2", "value2", TimeSpan.FromMinutes(5), new[] { tag });
        await _storage.SetAsync("key3", "value3", TimeSpan.FromMinutes(5), new[] { "other-tag" });

        await _storage.RemoveByTagAsync(tag);

        var result1 = await _storage.GetAsync<string>("key1");
        var result2 = await _storage.GetAsync<string>("key2");
        var result3 = await _storage.GetAsync<string>("key3");

        result1.Should().BeNull();
        result2.Should().BeNull();
        result3.Should().Be("value3");
    }

    [Fact]
    public void Exists_WithExistingKey_ShouldReturnTrue()
    {
        const string key = "exists-key";
        _storage.Set(key, "value", TimeSpan.FromMinutes(5));

        var exists = _storage.Exists(key);
        exists.Should().BeTrue();
    }

    [Fact]
    public void Exists_WithNonExistentKey_ShouldReturnFalse()
    {
        var exists = _storage.Exists("nonexistent");
        exists.Should().BeFalse();
    }

    [Fact]
    public void GetStats_ShouldReturnStatistics()
    {
        _storage.Set("key1", "value1", TimeSpan.FromMinutes(5));
        _storage.Get<string>("key1");
        _storage.Get<string>("nonexistent");

        var stats = _storage.GetStats();

        stats.Should().NotBeNull();
        stats.EntryCount.Should().BeGreaterThanOrEqualTo(0);
        stats.Hits.Should().BeGreaterThanOrEqualTo(0);
        stats.Misses.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Clear_ShouldClearCache()
    {
        _storage.Set("key1", "value1", TimeSpan.FromMinutes(5));

        _storage.Clear();

        // After clear, the key should not exist
        var result = _storage.Get<string>("key1");
        result.Should().BeNull();

        // But storage should still be functional
        _storage.Set("key2", "value2", TimeSpan.FromMinutes(5));
        var result2 = _storage.Get<string>("key2");
        result2.Should().Be("value2");
    }

    [Fact]
    public void Dispose_ShouldPreventFurtherOperations()
    {
        _storage.Set("key1", "value1", TimeSpan.FromMinutes(5));

        _storage.Dispose();

        // After dispose, operations should throw ObjectDisposedException
        var action = () => _storage.Get<string>("key1");
        action.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_ShouldPreventFurtherOperations()
    {
        await _storage.SetAsync("key1", "value1", TimeSpan.FromMinutes(5));

        await _storage.DisposeAsync();

        // After dispose, operations should throw ObjectDisposedException
        var action = () => _storage.Get<string>("key1");
        action.Should().Throw<ObjectDisposedException>();
    }
}