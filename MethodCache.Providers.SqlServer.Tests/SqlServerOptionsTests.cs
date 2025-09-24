using FluentAssertions;
using MethodCache.Providers.SqlServer.Configuration;

namespace MethodCache.Providers.SqlServer.Tests;

public class SqlServerOptionsTests
{
    [Fact]
    public void DefaultValues_ShouldBeSetCorrectly()
    {
        // Arrange & Act
        var options = new SqlServerOptions();

        // Assert
        options.ConnectionString.Should().Be(string.Empty);
        options.Schema.Should().Be("cache");
        options.EntriesTableName.Should().Be("Entries");
        options.TagsTableName.Should().Be("Tags");
        options.InvalidationsTableName.Should().Be("Invalidations");
        options.KeyPrefix.Should().Be("methodcache:");
        options.DefaultSerializer.Should().Be(SqlServerSerializerType.MessagePack);
        options.EnableAutoTableCreation.Should().BeTrue();
        options.CommandTimeoutSeconds.Should().Be(30);
        options.ConnectionTimeoutSeconds.Should().Be(15);
        options.MaxRetryAttempts.Should().Be(3);
        options.RetryBaseDelay.Should().Be(TimeSpan.FromMilliseconds(500));
        options.RetryBackoffType.Should().Be(SqlServerRetryBackoffType.Exponential);
        options.CircuitBreakerFailureRatio.Should().Be(0.5);
        options.CircuitBreakerMinimumThroughput.Should().Be(10);
        options.CircuitBreakerBreakDuration.Should().Be(TimeSpan.FromSeconds(30));
        options.CleanupInterval.Should().Be(TimeSpan.FromMinutes(5));
        options.CleanupBatchSize.Should().Be(1000);
        options.EnableBackgroundCleanup.Should().BeTrue();
        options.EnableDetailedLogging.Should().BeFalse();
        options.BackplanePollingInterval.Should().Be(TimeSpan.FromSeconds(2));
        options.BackplaneMessageRetention.Should().Be(TimeSpan.FromHours(1));
        options.EnableBackplane.Should().BeFalse();
    }

    [Fact]
    public void FullEntriesTableName_ShouldCombineSchemaAndTableName()
    {
        // Arrange
        var options = new SqlServerOptions
        {
            Schema = "test_schema",
            EntriesTableName = "test_entries"
        };

        // Act
        var fullName = options.FullEntriesTableName;

        // Assert
        fullName.Should().Be("[test_schema].[test_entries]");
    }

    [Fact]
    public void FullTagsTableName_ShouldCombineSchemaAndTableName()
    {
        // Arrange
        var options = new SqlServerOptions
        {
            Schema = "test_schema",
            TagsTableName = "test_tags"
        };

        // Act
        var fullName = options.FullTagsTableName;

        // Assert
        fullName.Should().Be("[test_schema].[test_tags]");
    }

    [Fact]
    public void FullInvalidationsTableName_ShouldCombineSchemaAndTableName()
    {
        // Arrange
        var options = new SqlServerOptions
        {
            Schema = "test_schema",
            InvalidationsTableName = "test_invalidations"
        };

        // Act
        var fullName = options.FullInvalidationsTableName;

        // Assert
        fullName.Should().Be("[test_schema].[test_invalidations]");
    }

    [Fact]
    public void Properties_ShouldBeSettable()
    {
        // Arrange
        var options = new SqlServerOptions();

        // Act
        options.ConnectionString = "test-connection";
        options.Schema = "custom_schema";
        options.EntriesTableName = "custom_entries";
        options.TagsTableName = "custom_tags";
        options.InvalidationsTableName = "custom_invalidations";
        options.KeyPrefix = "custom:";
        options.DefaultSerializer = SqlServerSerializerType.Json;
        options.EnableAutoTableCreation = false;
        options.CommandTimeoutSeconds = 60;
        options.ConnectionTimeoutSeconds = 30;
        options.MaxRetryAttempts = 5;
        options.RetryBaseDelay = TimeSpan.FromSeconds(1);
        options.RetryBackoffType = SqlServerRetryBackoffType.Linear;
        options.CircuitBreakerFailureRatio = 0.7;
        options.CircuitBreakerMinimumThroughput = 20;
        options.CircuitBreakerBreakDuration = TimeSpan.FromMinutes(1);
        options.CleanupInterval = TimeSpan.FromMinutes(10);
        options.CleanupBatchSize = 500;
        options.EnableBackgroundCleanup = false;
        options.EnableDetailedLogging = true;
        options.BackplanePollingInterval = TimeSpan.FromSeconds(5);
        options.BackplaneMessageRetention = TimeSpan.FromHours(2);
        options.EnableBackplane = true;

        // Assert
        options.ConnectionString.Should().Be("test-connection");
        options.Schema.Should().Be("custom_schema");
        options.EntriesTableName.Should().Be("custom_entries");
        options.TagsTableName.Should().Be("custom_tags");
        options.InvalidationsTableName.Should().Be("custom_invalidations");
        options.KeyPrefix.Should().Be("custom:");
        options.DefaultSerializer.Should().Be(SqlServerSerializerType.Json);
        options.EnableAutoTableCreation.Should().BeFalse();
        options.CommandTimeoutSeconds.Should().Be(60);
        options.ConnectionTimeoutSeconds.Should().Be(30);
        options.MaxRetryAttempts.Should().Be(5);
        options.RetryBaseDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.RetryBackoffType.Should().Be(SqlServerRetryBackoffType.Linear);
        options.CircuitBreakerFailureRatio.Should().Be(0.7);
        options.CircuitBreakerMinimumThroughput.Should().Be(20);
        options.CircuitBreakerBreakDuration.Should().Be(TimeSpan.FromMinutes(1));
        options.CleanupInterval.Should().Be(TimeSpan.FromMinutes(10));
        options.CleanupBatchSize.Should().Be(500);
        options.EnableBackgroundCleanup.Should().BeFalse();
        options.EnableDetailedLogging.Should().BeTrue();
        options.BackplanePollingInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.BackplaneMessageRetention.Should().Be(TimeSpan.FromHours(2));
        options.EnableBackplane.Should().BeTrue();
    }

    [Theory]
    [InlineData(SqlServerSerializerType.Json)]
    [InlineData(SqlServerSerializerType.MessagePack)]
    [InlineData(SqlServerSerializerType.Binary)]
    public void DefaultSerializer_ShouldAcceptAllValidValues(SqlServerSerializerType serializerType)
    {
        // Arrange
        var options = new SqlServerOptions();

        // Act
        options.DefaultSerializer = serializerType;

        // Assert
        options.DefaultSerializer.Should().Be(serializerType);
    }

    [Theory]
    [InlineData(SqlServerRetryBackoffType.Linear)]
    [InlineData(SqlServerRetryBackoffType.Exponential)]
    public void RetryBackoffType_ShouldAcceptAllValidValues(SqlServerRetryBackoffType backoffType)
    {
        // Arrange
        var options = new SqlServerOptions();

        // Act
        options.RetryBackoffType = backoffType;

        // Assert
        options.RetryBackoffType.Should().Be(backoffType);
    }
}

// Note: SqlServerHybridCacheOptions tests are defined with the SqlServerServiceCollectionExtensions
// since that's where the class is defined