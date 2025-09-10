using FluentAssertions;
using MethodCache.Providers.Redis.MultiRegion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.Providers.Redis.Tests.MultiRegion;

public class RegionSelectorTests
{
    private readonly MultiRegionOptions _options;
    private readonly RegionSelector _regionSelector;

    public RegionSelectorTests()
    {
        _options = new MultiRegionOptions
        {
            PrimaryRegion = "us-east-1",
            FailoverStrategy = RegionFailoverStrategy.PriorityBased,
            Regions = new List<RegionConfiguration>
            {
                new() { Name = "us-east-1", Priority = 10, IsPrimary = true },
                new() { Name = "us-west-2", Priority = 8 },
                new() { Name = "eu-west-1", Priority = 5 }
            }
        };

        _regionSelector = new RegionSelector(_options);
    }

    [Fact]
    public async Task SelectRegionForReadAsync_WithPriorityBasedStrategy_ShouldSelectHighestPriorityHealthyRegion()
    {
        // Arrange
        var regions = new[] { "us-east-1", "us-west-2", "eu-west-1" };
        
        // Mark all regions as healthy
        await _regionSelector.UpdateRegionHealthAsync("us-east-1", new RegionHealthStatus { Region = "us-east-1", IsHealthy = true });
        await _regionSelector.UpdateRegionHealthAsync("us-west-2", new RegionHealthStatus { Region = "us-west-2", IsHealthy = true });
        await _regionSelector.UpdateRegionHealthAsync("eu-west-1", new RegionHealthStatus { Region = "eu-west-1", IsHealthy = true });

        // Act
        var selectedRegion = await _regionSelector.SelectRegionForReadAsync("test-key", regions);

        // Assert
        selectedRegion.Should().Be("us-east-1"); // Highest priority
    }

    [Fact]
    public async Task SelectRegionForReadAsync_WithUnhealthyPrimaryRegion_ShouldSelectNextHealthyRegion()
    {
        // Arrange
        var regions = new[] { "us-east-1", "us-west-2", "eu-west-1" };
        
        // Mark primary as unhealthy, others as healthy
        await _regionSelector.UpdateRegionHealthAsync("us-east-1", new RegionHealthStatus { Region = "us-east-1", IsHealthy = false });
        await _regionSelector.UpdateRegionHealthAsync("us-west-2", new RegionHealthStatus { Region = "us-west-2", IsHealthy = true });
        await _regionSelector.UpdateRegionHealthAsync("eu-west-1", new RegionHealthStatus { Region = "eu-west-1", IsHealthy = true });

        // Act
        var selectedRegion = await _regionSelector.SelectRegionForReadAsync("test-key", regions);

        // Assert
        selectedRegion.Should().Be("us-west-2"); // Next highest priority healthy region
    }

    [Fact]
    public async Task SelectRegionForWriteAsync_WithHealthyPrimary_ShouldSelectPrimary()
    {
        // Arrange
        var regions = new[] { "us-east-1", "us-west-2", "eu-west-1" };
        
        // Mark primary as healthy
        await _regionSelector.UpdateRegionHealthAsync("us-east-1", new RegionHealthStatus { Region = "us-east-1", IsHealthy = true });

        // Act
        var selectedRegion = await _regionSelector.SelectRegionForWriteAsync("test-key", regions);

        // Assert
        selectedRegion.Should().Be("us-east-1"); // Primary region
    }

    [Fact]
    public async Task SelectRegionForWriteAsync_WithUnhealthyPrimary_ShouldFallbackToReadStrategy()
    {
        // Arrange
        var regions = new[] { "us-east-1", "us-west-2", "eu-west-1" };
        
        // Mark primary as unhealthy, others as healthy
        await _regionSelector.UpdateRegionHealthAsync("us-east-1", new RegionHealthStatus { Region = "us-east-1", IsHealthy = false });
        await _regionSelector.UpdateRegionHealthAsync("us-west-2", new RegionHealthStatus { Region = "us-west-2", IsHealthy = true });
        await _regionSelector.UpdateRegionHealthAsync("eu-west-1", new RegionHealthStatus { Region = "eu-west-1", IsHealthy = true });

        // Act
        var selectedRegion = await _regionSelector.SelectRegionForWriteAsync("test-key", regions);

        // Assert
        selectedRegion.Should().Be("us-west-2"); // Falls back to highest priority healthy region
    }

    [Fact]
    public async Task SelectRegionsForReplicationAsync_ShouldExcludeSourceRegion()
    {
        // Arrange
        var sourceRegion = "us-east-1";
        var regions = new[] { "us-east-1", "us-west-2", "eu-west-1" };
        
        // Mark all regions as healthy
        await _regionSelector.UpdateRegionHealthAsync("us-east-1", new RegionHealthStatus { Region = "us-east-1", IsHealthy = true });
        await _regionSelector.UpdateRegionHealthAsync("us-west-2", new RegionHealthStatus { Region = "us-west-2", IsHealthy = true });
        await _regionSelector.UpdateRegionHealthAsync("eu-west-1", new RegionHealthStatus { Region = "eu-west-1", IsHealthy = true });

        // Act
        var selectedRegions = await _regionSelector.SelectRegionsForReplicationAsync("test-key", sourceRegion, regions);

        // Assert
        selectedRegions.Should().NotContain(sourceRegion);
        selectedRegions.Should().Contain("us-west-2");
        selectedRegions.Should().Contain("eu-west-1");
    }

    [Fact]
    public async Task SelectRegionsForReplicationAsync_ShouldLimitToMaxConcurrentSyncs()
    {
        // Arrange
        var sourceRegion = "us-east-1";
        var regions = new[] { "us-east-1", "us-west-2", "eu-west-1", "ap-south-1", "ap-northeast-1" };
        _options.MaxConcurrentSyncs = 2;

        // Mark all regions as healthy
        foreach (var region in regions)
        {
            await _regionSelector.UpdateRegionHealthAsync(region, new RegionHealthStatus { Region = region, IsHealthy = true });
        }

        // Act
        var selectedRegions = await _regionSelector.SelectRegionsForReplicationAsync("test-key", sourceRegion, regions);

        // Assert
        selectedRegions.Should().HaveCountLessOrEqualTo(_options.MaxConcurrentSyncs);
        selectedRegions.Should().NotContain(sourceRegion);
    }

    [Fact]
    public async Task GetHealthyRegionsAsync_ShouldReturnOnlyHealthyRegions()
    {
        // Arrange
        await _regionSelector.UpdateRegionHealthAsync("us-east-1", new RegionHealthStatus { Region = "us-east-1", IsHealthy = true });
        await _regionSelector.UpdateRegionHealthAsync("us-west-2", new RegionHealthStatus { Region = "us-west-2", IsHealthy = false });
        await _regionSelector.UpdateRegionHealthAsync("eu-west-1", new RegionHealthStatus { Region = "eu-west-1", IsHealthy = true });

        // Act
        var healthyRegions = await _regionSelector.GetHealthyRegionsAsync();

        // Assert
        healthyRegions.Should().Contain("us-east-1");
        healthyRegions.Should().NotContain("us-west-2");
        healthyRegions.Should().Contain("eu-west-1");
    }

    [Fact]
    public async Task UpdateRegionHealthAsync_ShouldUpdateHealthStatus()
    {
        // Arrange
        var region = "us-east-1";
        var health = new RegionHealthStatus
        {
            Region = region,
            IsHealthy = true,
            Latency = TimeSpan.FromMilliseconds(50),
            LastChecked = DateTime.UtcNow
        };

        // Act
        await _regionSelector.UpdateRegionHealthAsync(region, health);
        var retrievedHealth = await _regionSelector.GetRegionHealthAsync(region);

        // Assert
        retrievedHealth.Should().NotBeNull();
        retrievedHealth!.IsHealthy.Should().Be(health.IsHealthy);
        retrievedHealth.Latency.Should().Be(health.Latency);
        retrievedHealth.Region.Should().Be(health.Region);
    }

    [Fact]
    public async Task SelectRegionForReadAsync_WithLatencyBasedStrategy_ShouldSelectLowestLatencyRegion()
    {
        // Arrange
        _options.FailoverStrategy = RegionFailoverStrategy.LatencyBased;
        var selector = new RegionSelector(_options);
        var regions = new[] { "us-east-1", "us-west-2", "eu-west-1" };

        // Set up health with different latencies
        await selector.UpdateRegionHealthAsync("us-east-1", new RegionHealthStatus 
        { 
            Region = "us-east-1", 
            IsHealthy = true, 
            Latency = TimeSpan.FromMilliseconds(100) 
        });
        await selector.UpdateRegionHealthAsync("us-west-2", new RegionHealthStatus 
        { 
            Region = "us-west-2", 
            IsHealthy = true, 
            Latency = TimeSpan.FromMilliseconds(50) 
        });
        await selector.UpdateRegionHealthAsync("eu-west-1", new RegionHealthStatus 
        { 
            Region = "eu-west-1", 
            IsHealthy = true, 
            Latency = TimeSpan.FromMilliseconds(200) 
        });

        // Act
        var selectedRegion = await selector.SelectRegionForReadAsync("test-key", regions);

        // Assert
        selectedRegion.Should().Be("us-west-2"); // Lowest latency
    }
}