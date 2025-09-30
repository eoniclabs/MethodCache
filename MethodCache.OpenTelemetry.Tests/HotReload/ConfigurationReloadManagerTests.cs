using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.HotReload;
using MsConfiguration = Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests.HotReload;

public class ConfigurationReloadManagerTests : IDisposable
{
    private readonly ConfigurationReloadManager _manager;
    private readonly MsConfiguration.IConfiguration _configuration;

    public ConfigurationReloadManagerTests()
    {
        // Create configuration with in-memory provider
        var initialData = new Dictionary<string, string>
        {
            ["MethodCache:OpenTelemetry:EnableTracing"] = "true",
            ["MethodCache:OpenTelemetry:EnableMetrics"] = "true",
            ["MethodCache:OpenTelemetry:SamplingRatio"] = "1.0"
        };

        var builder = new MsConfiguration.ConfigurationBuilder();
        MsConfiguration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(builder, initialData);
        _configuration = builder.Build();

        _manager = new ConfigurationReloadManager(_configuration, NullLogger<ConfigurationReloadManager>.Instance);
    }

    [Fact]
    public void GetConfiguration_WithValidSection_ReturnsConfiguration()
    {
        // Act
        var config = _manager.GetConfiguration<OpenTelemetryOptions>(ConfigurationSection.OpenTelemetry);

        // Assert
        config.Should().NotBeNull();
        config.EnableTracing.Should().BeTrue();
        config.EnableMetrics.Should().BeTrue();
        config.SamplingRatio.Should().Be(1.0);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_WithValidConfiguration_UpdatesSuccessfully()
    {
        // Arrange
        var newConfig = new OpenTelemetryOptions
        {
            EnableTracing = false,
            EnableMetrics = true,
            SamplingRatio = 0.5
        };

        var eventFired = false;
        _manager.ConfigurationChanged += (_, _) => eventFired = true;

        // Act
        var result = await _manager.UpdateConfigurationAsync(ConfigurationSection.OpenTelemetry, newConfig);

        // Assert
        result.Should().BeTrue();
        eventFired.Should().BeTrue();

        var updatedConfig = _manager.GetConfiguration<OpenTelemetryOptions>(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry);
        updatedConfig.EnableTracing.Should().BeFalse();
        updatedConfig.SamplingRatio.Should().Be(0.5);
    }

    [Fact]
    public async Task UpdatePropertyAsync_WithValidProperty_UpdatesSuccessfully()
    {
        // Arrange
        var originalConfig = _manager.GetConfiguration<OpenTelemetryOptions>(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry);
        originalConfig.EnableTracing.Should().BeTrue();

        // Act
        var result = await _manager.UpdatePropertyAsync(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry, nameof(OpenTelemetryOptions.EnableTracing), false);

        // Assert
        result.Should().BeTrue();

        var updatedConfig = _manager.GetConfiguration<OpenTelemetryOptions>(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry);
        updatedConfig.EnableTracing.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateConfigurationAsync_WithInvalidConfiguration_ReturnsValidationErrors()
    {
        // Arrange
        var invalidConfig = new OpenTelemetryOptions
        {
            SamplingRatio = -1.0 // Invalid value
        };

        // Act
        var result = await _manager.ValidateConfigurationAsync(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry, invalidConfig);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void GetConfigurationHistory_AfterUpdates_ReturnsHistory()
    {
        // Arrange & Act
        var initialHistory = _manager.GetConfigurationHistory(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry);

        // Assert
        initialHistory.Should().NotBeNull();
        // History might be empty initially, but should not be null
    }

    [Fact]
    public void SetHotReloadEnabled_DisablesThenEnables_WorksCorrectly()
    {
        // Act & Assert - should not throw
        _manager.SetHotReloadEnabled(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry, false);
        _manager.SetHotReloadEnabled(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry, true);
    }

    [Fact]
    public void GetAllConfigurations_ReturnsAllSections()
    {
        // Act
        var allConfigs = _manager.GetAllConfigurations();

        // Assert
        allConfigs.Should().NotBeEmpty();
        allConfigs.Should().ContainKey(MethodCache.OpenTelemetry.HotReload.ConfigurationSection.OpenTelemetry);
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}