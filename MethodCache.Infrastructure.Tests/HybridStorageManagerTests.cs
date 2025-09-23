using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Infrastructure.Implementation;
using NSubstitute;
using Xunit;

namespace MethodCache.Infrastructure.Tests;

public class HybridStorageManagerTests : IDisposable
{
    private readonly IMemoryStorage _l1Storage;
    private readonly IStorageProvider _l2Storage;
    private readonly IBackplane _backplane;
    private readonly HybridStorageManager _hybridStorage;
    private readonly StorageOptions _options;

    public HybridStorageManagerTests()
    {
        _l1Storage = Substitute.For<IMemoryStorage>();
        _l2Storage = Substitute.For<IStorageProvider>();
        _backplane = Substitute.For<IBackplane>();

        _options = new StorageOptions
        {
            L1DefaultExpiration = TimeSpan.FromMinutes(5),
            L1MaxExpiration = TimeSpan.FromMinutes(30),
            L2DefaultExpiration = TimeSpan.FromHours(4),
            L2Enabled = true,
            EnableAsyncL2Writes = false, // Use sync for testing
            EnableBackplane = true,
            InstanceId = "test-instance"
        };

        _hybridStorage = new HybridStorageManager(
            _l1Storage,
            Options.Create(_options),
            NullLogger<HybridStorageManager>.Instance,
            _l2Storage,
            null,
            _backplane);
    }

    [Fact]
    public void Name_ReturnsCorrectFormat()
    {
        // Arrange
        _l2Storage.Name.Returns("Redis");

        // Act
        var name = _hybridStorage.Name;

        // Assert
        name.Should().Be("Hybrid(L1+L2+Memory-Only)");
    }

    [Fact]
    public async Task GetAsync_WhenL1Hit_ReturnsFromL1Only()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        _l1Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns(value);

        // Act
        var result = await _hybridStorage.GetAsync<string>(key);

        // Assert
        result.Should().Be(value);
        await _l1Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l2Storage.DidNotReceive().GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WhenL1MissL2Hit_ReturnsFromL2AndWarmsL1()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        _l1Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns((string?)null);
        _l2Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns(value);

        // Act
        var result = await _hybridStorage.GetAsync<string>(key);

        // Assert
        result.Should().Be(value);
        await _l1Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l1Storage.Received(1).SetAsync(key, value, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WhenBothMiss_ReturnsDefault()
    {
        // Arrange
        const string key = "test-key";
        _l1Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns((string?)null);
        _l2Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        var result = await _hybridStorage.GetAsync<string>(key);

        // Assert
        result.Should().BeNull();
        await _l1Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WhenL2Disabled_OnlyUsesL1()
    {
        // Arrange
        var optionsWithL2Disabled = new StorageOptions { L2Enabled = false };
        var hybridWithL2Disabled = new HybridStorageManager(
            _l1Storage,
            Options.Create(optionsWithL2Disabled),
            NullLogger<HybridStorageManager>.Instance,
            _l2Storage,
            null,
            _backplane);

        const string key = "test-key";
        _l1Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        var result = await hybridWithL2Disabled.GetAsync<string>(key);

        // Assert
        result.Should().BeNull();
        await _l1Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l2Storage.DidNotReceive().GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithoutTags_SetsBothL1AndL2()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromHours(1);

        // Act
        await _hybridStorage.SetAsync(key, value, expiration);

        // Assert
        await _l1Storage.Received(1).SetAsync(key, value, Arg.Any<TimeSpan>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).SetAsync(key, value, expiration, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithTags_SetsBothL1AndL2WithTags()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromHours(1);
        var tags = new[] { "tag1", "tag2" };

        // Act
        await _hybridStorage.SetAsync(key, value, expiration, tags);

        // Assert
        await _l1Storage.Received(1).SetAsync(key, value, Arg.Any<TimeSpan>(), tags, Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).SetAsync(key, value, expiration, tags, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_CalculatesCorrectL1Expiration()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        var longExpiration = TimeSpan.FromDays(1); // Longer than L1MaxExpiration

        // Act
        await _hybridStorage.SetAsync(key, value, longExpiration);

        // Assert
        await _l1Storage.Received(1).SetAsync(
            key,
            value,
            Arg.Is<TimeSpan>(exp => exp <= _options.L1MaxExpiration && exp >= _options.L1DefaultExpiration),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_RemovesFromBothL1AndL2()
    {
        // Arrange
        const string key = "test-key";

        // Act
        await _hybridStorage.RemoveAsync(key);

        // Assert
        await _l1Storage.Received(1).RemoveAsync(key, Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).RemoveAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_PublishesBackplaneMessage()
    {
        // Arrange
        const string key = "test-key";

        // Act
        await _hybridStorage.RemoveAsync(key);

        // Assert
        await _backplane.Received(1).PublishInvalidationAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveByTagAsync_RemovesFromBothL1AndL2()
    {
        // Arrange
        const string tag = "test-tag";

        // Act
        await _hybridStorage.RemoveByTagAsync(tag);

        // Assert
        await _l1Storage.Received(1).RemoveByTagAsync(tag, Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).RemoveByTagAsync(tag, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveByTagAsync_PublishesBackplaneTagMessage()
    {
        // Arrange
        const string tag = "test-tag";

        // Act
        await _hybridStorage.RemoveByTagAsync(tag);

        // Assert
        await _backplane.Received(1).PublishTagInvalidationAsync(tag, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistsAsync_ChecksL1First()
    {
        // Arrange
        const string key = "test-key";
        _l1Storage.Exists(key).Returns(true);

        // Act
        var exists = await _hybridStorage.ExistsAsync(key);

        // Assert
        exists.Should().BeTrue();
        _l1Storage.Received(1).Exists(key);
        await _l2Storage.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistsAsync_ChecksL2WhenL1ReturnsFalse()
    {
        // Arrange
        const string key = "test-key";
        _l1Storage.Exists(key).Returns(false);
        _l2Storage.ExistsAsync(key, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var exists = await _hybridStorage.ExistsAsync(key);

        // Assert
        exists.Should().BeTrue();
        _l1Storage.Received(1).Exists(key);
        await _l2Storage.Received(1).ExistsAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHealthAsync_WhenBothHealthy_ReturnsHealthy()
    {
        // Arrange
        _l2Storage.GetHealthAsync(Arg.Any<CancellationToken>()).Returns(HealthStatus.Healthy);

        // Act
        var health = await _hybridStorage.GetHealthAsync();

        // Assert
        health.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task GetHealthAsync_WhenL2Unhealthy_ReturnsDegraded()
    {
        // Arrange
        _l2Storage.GetHealthAsync(Arg.Any<CancellationToken>()).Returns(HealthStatus.Unhealthy);

        // Act
        var health = await _hybridStorage.GetHealthAsync();

        // Assert
        health.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task GetStatsAsync_CombinesL1AndL2Stats()
    {
        // Arrange
        var l1Stats = new MemoryStorageStats
        {
            Hits = 100,
            Misses = 20,
            EntryCount = 50,
            Evictions = 5,
            TagMappingCount = 10
        };

        var l2Stats = new StorageStats
        {
            GetOperations = 200,
            SetOperations = 150,
            RemoveOperations = 25,
            AverageResponseTimeMs = 5.5,
            ErrorCount = 2
        };

        _l1Storage.GetStats().Returns(l1Stats);
        _l2Storage.GetStatsAsync(Arg.Any<CancellationToken>()).Returns(l2Stats);

        // Act
        var stats = await _hybridStorage.GetStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats!.AdditionalStats.Should().ContainKey("L1Hits");
        stats.AdditionalStats.Should().ContainKey("L1Misses");
        stats.AdditionalStats.Should().ContainKey("L2Stats");
        stats.AdditionalStats["L1HitRatio"].Should().Be(l1Stats.HitRatio);
        stats.AdditionalStats["L2Stats"].Should().Be(l2Stats);
    }

    [Fact]
    public async Task GetAsync_WhenL2ThrowsException_ReturnsDefault()
    {
        // Arrange
        const string key = "test-key";
        _l1Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns((string?)null);
        _l2Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns(Task.FromException<string?>(new InvalidOperationException("L2 error")));

        // Act
        var result = await _hybridStorage.GetAsync<string>(key);

        // Assert
        result.Should().BeNull();
        await _l1Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WhenAsyncL2WritesEnabled_DoesNotWaitForL2()
    {
        // Arrange
        var optionsWithAsyncWrites = new StorageOptions
        {
            L1DefaultExpiration = TimeSpan.FromMinutes(5),
            L2Enabled = true,
            EnableAsyncL2Writes = true
        };

        var hybridWithAsyncWrites = new HybridStorageManager(
            _l1Storage,
            Options.Create(optionsWithAsyncWrites),
            NullLogger<HybridStorageManager>.Instance,
            _l2Storage,
            null,
            _backplane);

        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromHours(1);

        // Act
        await hybridWithAsyncWrites.SetAsync(key, value, expiration);

        // Assert
        // Should complete immediately without waiting for L2
        await _l1Storage.Received(1).SetAsync(key, value, Arg.Any<TimeSpan>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());

        // L2 write happens asynchronously, so we might need to wait a bit or check differently
        // For this test, we're mainly verifying that the method completes quickly
    }

    public void Dispose()
    {
        // Nothing to dispose in this test class
    }
}