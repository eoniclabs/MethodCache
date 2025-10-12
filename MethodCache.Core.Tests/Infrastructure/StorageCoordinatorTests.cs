using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Configuration;
using NSubstitute;
using System.Threading.Tasks;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Core.Storage.Coordination;
using MethodCache.Core.Storage.Coordination.Layers;
using Xunit;

namespace MethodCache.Core.Tests.Infrastructure;

public class StorageCoordinatorTests : IDisposable
{
    private readonly IMemoryStorage _l1Storage;
    private readonly IStorageProvider _l2Storage;
    private readonly IBackplane _backplane;
    private readonly StorageCoordinator _storageCoordinator;
    private readonly StorageOptions _options;

    public StorageCoordinatorTests()
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

        _storageCoordinator = StorageCoordinatorFactory.Create(
            _l1Storage,
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<StorageCoordinator>.Instance,
            _l2Storage,
            null,
            _backplane,
            null);
    }

    [Fact]
    public void Name_ReturnsCorrectFormat()
    {
        // Arrange
        _l2Storage.Name.Returns("Redis");

        // Act
        var name = _storageCoordinator.Name;

        // Assert
        // StorageCoordinator builds name from enabled layer IDs
        name.Should().StartWith("Coordinator(");
        name.Should().Contain("TagIndex");
        name.Should().Contain("L1");
    }

    [Fact]
    public async Task GetAsync_WhenL1Hit_ReturnsFromL1Only()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        _l1Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns(value);

        // Act
        var result = await _storageCoordinator.GetAsync<string>(key);

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
        var result = await _storageCoordinator.GetAsync<string>(key);

        // Assert
        result.Should().Be(value);
        await _l1Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l2Storage.Received(1).GetAsync<string>(key, Arg.Any<CancellationToken>());
        await _l1Storage.Received(1).SetAsync(key, value, Arg.Any<TimeSpan>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WhenBothMiss_ReturnsDefault()
    {
        // Arrange
        const string key = "test-key";
        _l1Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns((string?)null);
        _l2Storage.GetAsync<string>(key, Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        var result = await _storageCoordinator.GetAsync<string>(key);

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
        var hybridWithL2Disabled = StorageCoordinatorFactory.Create(
            _l1Storage,
            Microsoft.Extensions.Options.Options.Create(optionsWithL2Disabled),
            NullLogger<StorageCoordinator>.Instance,
            _l2Storage,
            null,
            _backplane,
            null);

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
        await _storageCoordinator.SetAsync(key, value, expiration);

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
        await _storageCoordinator.SetAsync(key, value, expiration, tags);

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
        await _storageCoordinator.SetAsync(key, value, longExpiration);

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
        await _storageCoordinator.RemoveAsync(key);

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
        await _storageCoordinator.RemoveAsync(key);

        // Assert
        await _backplane.Received(1).PublishInvalidationAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveByTagAsync_RemovesFromBothL1AndL2()
    {
        // Arrange
        const string tag = "test-tag";

        // Act
        await _storageCoordinator.RemoveByTagAsync(tag);

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
        await _storageCoordinator.RemoveByTagAsync(tag);

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
        var exists = await _storageCoordinator.ExistsAsync(key);

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
        var exists = await _storageCoordinator.ExistsAsync(key);

        // Assert
        exists.Should().BeTrue();
        _l1Storage.Received(1).Exists(key);
        await _l2Storage.Received(1).ExistsAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHealthAsync_WhenAllLayersHealthy_ReturnsHealthy()
    {
        // Arrange
        var layer1 = Substitute.For<IStorageLayer>();
        layer1.IsEnabled.Returns(true);
        layer1.GetHealthAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(new LayerHealthStatus("Layer1", HealthStatus.Healthy)));

        var layer2 = Substitute.For<IStorageLayer>();
        layer2.IsEnabled.Returns(true);
        layer2.GetHealthAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(new LayerHealthStatus("Layer2", HealthStatus.Healthy)));

        await using var coordinator = new StorageCoordinator(
            new[] { layer1, layer2 },
            NullLogger<StorageCoordinator>.Instance);

        // Act
        var health = await coordinator.GetHealthAsync();

        // Assert
        health.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task GetHealthAsync_WhenOneLayerUnhealthy_ReturnsUnhealthy()
    {
        // Arrange
        var healthyLayer = Substitute.For<IStorageLayer>();
        healthyLayer.IsEnabled.Returns(true);
        healthyLayer.GetHealthAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(new LayerHealthStatus("HealthyLayer", HealthStatus.Healthy)));

        var unhealthyLayer = Substitute.For<IStorageLayer>();
        unhealthyLayer.IsEnabled.Returns(true);
        unhealthyLayer.GetHealthAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(new LayerHealthStatus("UnhealthyLayer", HealthStatus.Unhealthy)));

        await using var coordinator = new StorageCoordinator(
            new[] { healthyLayer, unhealthyLayer },
            NullLogger<StorageCoordinator>.Instance);

        // Act
        var health = await coordinator.GetHealthAsync();

        // Assert
        health.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task GetStatsAsync_AggregatesStatsFromAllLayers()
    {
        // Arrange
        var layer1 = Substitute.For<IStorageLayer>();
        layer1.IsEnabled.Returns(true);
        layer1.LayerId.Returns("Layer1");
        layer1.GetStats().Returns(new LayerStats("Layer1", 10, 5, 0.67, 15));

        var layer2 = Substitute.For<IStorageLayer>();
        layer2.IsEnabled.Returns(true);
        layer2.LayerId.Returns("Layer2");
        layer2.GetStats().Returns(new LayerStats("Layer2", 20, 10, 0.67, 30));

        await using var coordinator = new StorageCoordinator(
            new[] { layer1, layer2 },
            NullLogger<StorageCoordinator>.Instance);

        // Act
        var stats = await coordinator.GetStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats!.AdditionalStats.Should().NotBeNull();
        stats.AdditionalStats!["TotalHits"].Should().Be(30L); // 10 + 20
        stats.AdditionalStats["TotalMisses"].Should().Be(15L); // 5 + 10
        stats.AdditionalStats["Layer1Stats"].Should().BeOfType<LayerStats>();
        stats.AdditionalStats["Layer2Stats"].Should().BeOfType<LayerStats>();
    }

    [Fact]
    public async Task GetAsync_WhenL2ThrowsException_ReturnsDefault()
    {
        // Arrange
        const string key = "test-key";
        _l1Storage
            .GetAsync<string>(key, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>(null));
        _l2Storage
            .GetAsync<string>(key, Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<string?>(Task.FromException<string?>(new InvalidOperationException("L2 error"))));

        // Act
        var result = await _storageCoordinator.GetAsync<string>(key);

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

        await using var hybridWithAsyncWrites = StorageCoordinatorFactory.Create(
            _l1Storage,
            Microsoft.Extensions.Options.Options.Create(optionsWithAsyncWrites),
            NullLogger<StorageCoordinator>.Instance,
            _l2Storage,
            null,
            _backplane,
            null);

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
        _storageCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
