using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Providers.Memory.Configuration;
using MethodCache.Providers.Memory.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.Providers.Memory.Tests;

public class AdvancedMemoryStorageProviderTests
{
    private readonly AdvancedMemoryStorageProvider _provider;
    private readonly AdvancedMemoryOptions _options;

    public AdvancedMemoryStorageProviderTests()
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
        var logger = serviceProvider.GetRequiredService<ILogger<AdvancedMemoryStorageProvider>>();

        _provider = new AdvancedMemoryStorageProvider(
            Options.Create(_options),
            logger);
    }

    [Fact]
    public void Name_ShouldReturnAdvancedMemory()
    {
        _provider.Name.Should().Be("AdvancedMemory");
    }

    [Fact]
    public async Task GetAsync_WithNonExistentKey_ShouldReturnDefault()
    {
        var result = await _provider.GetAsync<string>("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAndGetAsync_ShouldStoreAndRetrieveValue()
    {
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);

        await _provider.SetAsync(key, value, expiration, new[] { "tag1", "tag2" });
        var result = await _provider.GetAsync<string>(key);

        result.Should().Be(value);
    }

    [Fact]
    public async Task SetAsync_WithoutTags_ShouldWork()
    {
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);

        await _provider.SetAsync(key, value, expiration);
        var result = await _provider.GetAsync<string>(key);

        result.Should().Be(value);
    }

    [Fact]
    public async Task GetAsync_WithExpiredEntry_ShouldReturnDefault()
    {
        const string key = "expired-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMilliseconds(1);

        await _provider.SetAsync(key, value, expiration);
        await Task.Delay(10); // Wait for expiration

        var result = await _provider.GetAsync<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveEntry()
    {
        const string key = "remove-key";
        const string value = "test-value";

        await _provider.SetAsync(key, value, TimeSpan.FromMinutes(5));
        await _provider.RemoveAsync(key);

        var result = await _provider.GetAsync<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveByTagAsync_ShouldRemoveEntriesWithTag()
    {
        const string tag = "test-tag";

        await _provider.SetAsync("key1", "value1", TimeSpan.FromMinutes(5), new[] { tag });
        await _provider.SetAsync("key2", "value2", TimeSpan.FromMinutes(5), new[] { tag });
        await _provider.SetAsync("key3", "value3", TimeSpan.FromMinutes(5), new[] { "other-tag" });

        await _provider.RemoveByTagAsync(tag);

        var result1 = await _provider.GetAsync<string>("key1");
        var result2 = await _provider.GetAsync<string>("key2");
        var result3 = await _provider.GetAsync<string>("key3");

        result1.Should().BeNull();
        result2.Should().BeNull();
        result3.Should().Be("value3");
    }

    [Fact]
    public async Task ExistsAsync_WithExistingKey_ShouldReturnTrue()
    {
        const string key = "exists-key";
        await _provider.SetAsync(key, "value", TimeSpan.FromMinutes(5));

        var exists = await _provider.ExistsAsync(key);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_WithNonExistentKey_ShouldReturnFalse()
    {
        var exists = await _provider.ExistsAsync("nonexistent");
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_WithExpiredKey_ShouldReturnFalse()
    {
        const string key = "expired-key";
        await _provider.SetAsync(key, "value", TimeSpan.FromMilliseconds(1));
        await Task.Delay(10);

        var exists = await _provider.ExistsAsync(key);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task GetHealthAsync_ShouldReturnHealthy()
    {
        // Add some cache activity to improve hit ratio
        await _provider.SetAsync("key1", "value1", TimeSpan.FromMinutes(5));
        await _provider.GetAsync<string>("key1");
        await _provider.GetAsync<string>("key1");

        var health = await _provider.GetHealthAsync();
        health.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task GetStatsAsync_ShouldReturnStatistics()
    {
        await _provider.SetAsync("key1", "value1", TimeSpan.FromMinutes(5));
        await _provider.GetAsync<string>("key1");
        await _provider.GetAsync<string>("nonexistent");

        var stats = await _provider.GetStatsAsync();

        stats.Should().NotBeNull();
        stats!.AdditionalStats.Should().ContainKey("Hits");
        stats.AdditionalStats.Should().ContainKey("Misses");
        stats.AdditionalStats.Should().ContainKey("EntryCount");
        stats.AdditionalStats.Should().ContainKey("HitRatio");
    }

    [Theory]
    [InlineData(EvictionPolicy.LRU)]
    [InlineData(EvictionPolicy.LFU)]
    [InlineData(EvictionPolicy.TTL)]
    [InlineData(EvictionPolicy.Random)]
    public async Task Eviction_WithDifferentPolicies_ShouldWork(EvictionPolicy policy)
    {
        var options = new AdvancedMemoryOptions
        {
            MaxEntries = 2,
            EvictionPolicy = policy
        };

        var services = new ServiceCollection();
        services.AddLogging();
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<AdvancedMemoryStorageProvider>>();

        var provider = new AdvancedMemoryStorageProvider(Options.Create(options), logger);

        // Fill cache beyond capacity
        await provider.SetAsync("key1", "value1", TimeSpan.FromMinutes(5));
        await provider.SetAsync("key2", "value2", TimeSpan.FromMinutes(5));
        await provider.SetAsync("key3", "value3", TimeSpan.FromMinutes(5)); // Should trigger eviction

        var stats = await provider.GetStatsAsync();
        var entryCount = Convert.ToInt64(stats!.AdditionalStats["EntryCount"]);
        entryCount.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetAsync_WithWrongType_ShouldReturnDefault()
    {
        const string key = "type-test";
        await _provider.SetAsync(key, "string-value", TimeSpan.FromMinutes(5));

        var result = await _provider.GetAsync<int>(key);
        result.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentOperations_ShouldBeThreadSafe()
    {
        var tasks = new List<Task>();

        // Start multiple concurrent operations
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                await _provider.SetAsync($"key{index}", $"value{index}", TimeSpan.FromMinutes(5));
                await _provider.GetAsync<string>($"key{index}");
            }));
        }

        await Task.WhenAll(tasks);

        var stats = await _provider.GetStatsAsync();
        var entryCount = Convert.ToInt64(stats!.AdditionalStats["EntryCount"]);
        entryCount.Should().Be(10);
    }
}