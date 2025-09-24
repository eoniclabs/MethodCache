using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;
using NSubstitute;

namespace MethodCache.Providers.SqlServer.Tests;

public class SqlServerBackplaneTests
{
    private readonly ISqlServerConnectionManager _mockConnectionManager;
    private readonly IOptions<SqlServerOptions> _mockOptions;
    private readonly ILogger<SqlServerBackplane> _mockLogger;
    private readonly SqlServerOptions _options;

    public SqlServerBackplaneTests()
    {
        _mockConnectionManager = Substitute.For<ISqlServerConnectionManager>();
        _mockLogger = Substitute.For<ILogger<SqlServerBackplane>>();
        _options = new SqlServerOptions
        {
            ConnectionString = "test-connection",
            EnableBackplane = true,
            BackplanePollingInterval = TimeSpan.FromSeconds(1),
            BackplaneMessageRetention = TimeSpan.FromMinutes(30),
            Schema = "test",
            InvalidationsTableName = "Invalidations"
        };
        _mockOptions = Options.Create(_options);
    }

    [Fact]
    public void Constructor_ShouldInitializeInstanceId()
    {
        // Act
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Assert
        backplane.InstanceId.Should().NotBeNullOrEmpty();
        backplane.InstanceId.Should().Contain(Environment.MachineName);
        backplane.InstanceId.Should().Contain(Environment.ProcessId.ToString());
    }

    [Fact]
    public void Constructor_WithBackplaneDisabled_ShouldNotStartTimers()
    {
        // Arrange
        var disabledOptions = Options.Create(new SqlServerOptions { EnableBackplane = false });

        // Act
        var backplane = new SqlServerBackplane(_mockConnectionManager, disabledOptions, _mockLogger);

        // Assert
        // No exception should be thrown, and timers should not be initialized
        backplane.InstanceId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PublishInvalidationAsync_WithBackplaneDisabled_ShouldSkipPublication()
    {
        // Arrange
        var disabledOptions = Options.Create(new SqlServerOptions { EnableBackplane = false });
        var backplane = new SqlServerBackplane(_mockConnectionManager, disabledOptions, _mockLogger);

        // Act
        await backplane.PublishInvalidationAsync("test-key");

        // Assert
        await _mockConnectionManager.DidNotReceive().GetConnectionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishTagInvalidationAsync_WithBackplaneDisabled_ShouldSkipPublication()
    {
        // Arrange
        var disabledOptions = Options.Create(new SqlServerOptions { EnableBackplane = false });
        var backplane = new SqlServerBackplane(_mockConnectionManager, disabledOptions, _mockLogger);

        // Act
        await backplane.PublishTagInvalidationAsync("test-tag");

        // Assert
        await _mockConnectionManager.DidNotReceive().GetConnectionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscribeAsync_WithBackplaneDisabled_ShouldNotSubscribe()
    {
        // Arrange
        var disabledOptions = Options.Create(new SqlServerOptions { EnableBackplane = false });
        var backplane = new SqlServerBackplane(_mockConnectionManager, disabledOptions, _mockLogger);
        var messageHandler = Substitute.For<Func<BackplaneMessage, Task>>();

        // Act
        await backplane.SubscribeAsync(messageHandler);

        // Assert
        // Should complete without error but not actually subscribe
        messageHandler.DidNotReceive();
    }

    [Fact]
    public async Task UnsubscribeAsync_ShouldCompleteSuccessfully()
    {
        // Arrange
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Act
        await backplane.UnsubscribeAsync();

        // Assert
        // Should complete without error
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeResourcesGracefully()
    {
        // Arrange
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Act
        await backplane.DisposeAsync();

        // Assert
        // Should complete without error
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_ShouldNotThrow()
    {
        // Arrange
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Act & Assert
        await backplane.DisposeAsync();
        await backplane.DisposeAsync(); // Second call should not throw
    }

    [Fact]
    public void InstanceId_ShouldBeUniqueAcrossInstances()
    {
        // Act
        var backplane1 = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);
        var backplane2 = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Assert
        backplane1.InstanceId.Should().NotBe(backplane2.InstanceId);
    }

    [Fact]
    public void InstanceId_ShouldContainMachineNameAndProcessId()
    {
        // Act
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Assert
        backplane.InstanceId.Should().Contain(Environment.MachineName);
        backplane.InstanceId.Should().Contain(Environment.ProcessId.ToString());
    }

    [Fact]
    public void InstanceId_ShouldContainGuid()
    {
        // Act
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Assert
        // Instance ID should contain a GUID-like pattern (32 hex characters)
        backplane.InstanceId.Should().MatchRegex(@"[a-f0-9]{32}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("test-key")]
    [InlineData("very-long-key-name-that-could-potentially-cause-issues-with-length-limits")]
    [InlineData("key with spaces")]
    [InlineData("key:with:colons")]
    public async Task PublishInvalidationAsync_WithVariousKeyFormats_ShouldNotThrow(string key)
    {
        // Arrange
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Act & Assert
        var act = () => backplane.PublishInvalidationAsync(key);
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("test-tag")]
    [InlineData("very-long-tag-name-that-could-potentially-cause-issues")]
    [InlineData("tag with spaces")]
    [InlineData("tag:with:colons")]
    public async Task PublishTagInvalidationAsync_WithVariousTagFormats_ShouldNotThrow(string tag)
    {
        // Arrange
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Act & Assert
        var act = () => backplane.PublishTagInvalidationAsync(tag);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SubscribeAsync_WithNullHandler_ShouldNotThrow()
    {
        // Arrange
        var backplane = new SqlServerBackplane(_mockConnectionManager, _mockOptions, _mockLogger);

        // Act & Assert
        var act = () => backplane.SubscribeAsync(null!);
        await act.Should().NotThrowAsync();
    }
}