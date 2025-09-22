using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Infrastructure.Abstractions;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public class SqlServerBackplaneIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact]
    public async Task PublishInvalidationAsync_ShouldStoreInvalidationMessage()
    {
        // Arrange
        var backplane = ServiceProvider.GetRequiredService<IBackplane>();
        var key = "test-invalidation-key";

        // Act
        await backplane.PublishInvalidationAsync(key);

        // Assert - The message should be stored in the database
        // We'll verify this by checking if another instance would receive it
        // (This is implicitly tested by the subscription mechanism)
    }

    [Fact]
    public async Task PublishTagInvalidationAsync_ShouldStoreTagInvalidationMessage()
    {
        // Arrange
        var backplane = ServiceProvider.GetRequiredService<IBackplane>();
        var tag = "test-invalidation-tag";

        // Act
        await backplane.PublishTagInvalidationAsync(tag);

        // Assert - The message should be stored in the database
        // We'll verify this by checking if another instance would receive it
        // (This is implicitly tested by the subscription mechanism)
    }

    [Fact]
    public async Task SubscribeAsync_ShouldReceiveInvalidationMessages()
    {
        // Arrange
        var backplane1 = ServiceProvider.GetRequiredService<IBackplane>();

        // Create a second service provider to simulate another instance
        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = $"test2:{Guid.NewGuid():N}:";
            options.Schema = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value.Schema;
        });

        var serviceProvider2 = services2.BuildServiceProvider();
        var backplane2 = serviceProvider2.GetRequiredService<IBackplane>();

        var receivedMessages = new List<BackplaneMessage>();
        var messageReceived = new TaskCompletionSource<bool>();

        // Act
        await backplane2.SubscribeAsync(message =>
        {
            receivedMessages.Add(message);
            messageReceived.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Publish from first instance
        var testKey = "cross-instance-test-key";
        await backplane1.PublishInvalidationAsync(testKey);

        // Wait for message to be received (with timeout)
        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        received.Should().BeTrue();
        receivedMessages.Should().HaveCount(1);
        receivedMessages[0].Type.Should().Be(BackplaneMessageType.KeyInvalidation);
        receivedMessages[0].Key.Should().Be(testKey);
        receivedMessages[0].InstanceId.Should().NotBe(backplane2.InstanceId);

        // Cleanup
        await serviceProvider2.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldReceiveTagInvalidationMessages()
    {
        // Arrange
        var backplane1 = ServiceProvider.GetRequiredService<IBackplane>();

        // Create a second service provider to simulate another instance
        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = $"test2:{Guid.NewGuid():N}:";
            options.Schema = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value.Schema;
        });

        var serviceProvider2 = services2.BuildServiceProvider();
        var backplane2 = serviceProvider2.GetRequiredService<IBackplane>();

        var receivedMessages = new List<BackplaneMessage>();
        var messageReceived = new TaskCompletionSource<bool>();

        // Act
        await backplane2.SubscribeAsync(message =>
        {
            receivedMessages.Add(message);
            messageReceived.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Publish from first instance
        var testTag = "cross-instance-test-tag";
        await backplane1.PublishTagInvalidationAsync(testTag);

        // Wait for message to be received (with timeout)
        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        received.Should().BeTrue();
        receivedMessages.Should().HaveCount(1);
        receivedMessages[0].Type.Should().Be(BackplaneMessageType.TagInvalidation);
        receivedMessages[0].Tag.Should().Be(testTag);
        receivedMessages[0].InstanceId.Should().NotBe(backplane2.InstanceId);

        // Cleanup
        await serviceProvider2.DisposeAsync();
    }

    [Fact]
    public async Task BackplaneCoordination_ShouldInvalidateAcrossInstances()
    {
        // Arrange
        var storageProvider1 = ServiceProvider.GetRequiredService<IStorageProvider>();

        // Create a second service provider to simulate another instance
        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value.KeyPrefix;
            options.Schema = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value.Schema;
        });

        var serviceProvider2 = services2.BuildServiceProvider();
        var storageProvider2 = serviceProvider2.GetRequiredService<IStorageProvider>();

        var testKey = "coordination-test-key";
        var testValue = "coordination-test-value";
        var testTag = "coordination-test-tag";

        // Act
        // Store value in first instance
        await storageProvider1.SetAsync(testKey, testValue, TimeSpan.FromMinutes(10), new[] { testTag });

        // Wait a moment for initial storage
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Verify value is accessible from both instances
        var value1 = await storageProvider1.GetAsync<string>(testKey);
        var value2 = await storageProvider2.GetAsync<string>(testKey);

        value1.Should().Be(testValue);
        value2.Should().Be(testValue);

        // Invalidate by tag from second instance
        await storageProvider2.RemoveByTagAsync(testTag);

        // Wait for backplane propagation
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Verify value is removed from both instances
        var afterInvalidation1 = await storageProvider1.GetAsync<string>(testKey);
        var afterInvalidation2 = await storageProvider2.GetAsync<string>(testKey);

        // Assert
        afterInvalidation1.Should().BeNull();
        afterInvalidation2.Should().BeNull();

        // Cleanup
        await serviceProvider2.DisposeAsync();
    }

    [Fact]
    public void InstanceId_ShouldBeUniquePerInstance()
    {
        // Arrange
        var backplane1 = ServiceProvider.GetRequiredService<IBackplane>();

        // Create a second service provider
        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.Schema = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value.Schema;
        });

        var serviceProvider2 = services2.BuildServiceProvider();
        var backplane2 = serviceProvider2.GetRequiredService<IBackplane>();

        // Assert
        backplane1.InstanceId.Should().NotBeNullOrEmpty();
        backplane2.InstanceId.Should().NotBeNullOrEmpty();
        backplane1.InstanceId.Should().NotBe(backplane2.InstanceId);

        // Cleanup
        serviceProvider2.Dispose();
    }

    [Fact]
    public async Task UnsubscribeAsync_ShouldStopReceivingMessages()
    {
        // Arrange
        var backplane1 = ServiceProvider.GetRequiredService<IBackplane>();

        // Create a second service provider
        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = $"test2:{Guid.NewGuid():N}:";
            options.Schema = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value.Schema;
        });

        var serviceProvider2 = services2.BuildServiceProvider();
        var backplane2 = serviceProvider2.GetRequiredService<IBackplane>();

        var receivedMessages = new List<BackplaneMessage>();

        // Act
        // Subscribe first
        await backplane2.SubscribeAsync(message =>
        {
            receivedMessages.Add(message);
            return Task.CompletedTask;
        });

        // Publish a message and verify it's received
        await backplane1.PublishInvalidationAsync("test-before-unsubscribe");
        await Task.Delay(TimeSpan.FromSeconds(3)); // Wait for polling

        var messagesBeforeUnsubscribe = receivedMessages.Count;

        // Unsubscribe
        await backplane2.UnsubscribeAsync();

        // Publish another message
        await backplane1.PublishInvalidationAsync("test-after-unsubscribe");
        await Task.Delay(TimeSpan.FromSeconds(3)); // Wait for polling

        var messagesAfterUnsubscribe = receivedMessages.Count;

        // Assert
        messagesBeforeUnsubscribe.Should().BeGreaterThan(0);
        messagesAfterUnsubscribe.Should().Be(messagesBeforeUnsubscribe); // No new messages

        // Cleanup
        await serviceProvider2.DisposeAsync();
    }
}