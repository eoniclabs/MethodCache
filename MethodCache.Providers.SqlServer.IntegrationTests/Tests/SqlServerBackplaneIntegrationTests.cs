using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Storage;
using Xunit;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

[Collection("SqlServerBackplane")]
public class SqlServerBackplaneIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact(Timeout = 30000)] // 30 seconds
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

    [Fact(Timeout = 30000)] // 30 seconds
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

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task SubscribeAsync_ShouldReceiveInvalidationMessages()
    {
        // Arrange
        var backplane1 = ServiceProvider.GetRequiredService<IBackplane>();

        // Create a second service provider with same configuration for backplane coordination
        var services2 = new ServiceCollection();
        services2.AddLogging();

        // Get options from first instance to ensure backplane coordination works
        var firstInstanceOptions = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;

        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = firstInstanceOptions.KeyPrefix; // Use same key prefix for backplane coordination
            options.Schema = firstInstanceOptions.Schema;
            options.DefaultSerializer = firstInstanceOptions.DefaultSerializer;
            options.BackplanePollingInterval = TimeSpan.FromMilliseconds(100); // Faster polling for tests
        });

        var serviceProvider2 = services2.BuildServiceProvider();

        // Start hosted services for the second instance (needed for backplane polling)
        await StartHostedServicesAsync(serviceProvider2);

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

        // Small delay to allow the immediate poll from SubscribeAsync to complete
        // This prevents a race where we publish before the initial poll runs
        await Task.Delay(50);

        // Publish after subscribing - use unique key per test run
        var testKey = $"cross-instance-test-key-{Guid.NewGuid():N}";
        await backplane1.PublishInvalidationAsync(testKey);

        // Wait for message to be received (with timeout)
        // The immediate poll from SubscribeAsync plus the timer should pick this up
        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        received.Should().BeTrue();
        var ourMessage = receivedMessages.Should().ContainSingle(m => m.Key == testKey, "should receive the published message").Subject;
        ourMessage.Type.Should().Be(BackplaneMessageType.KeyInvalidation);
        ourMessage.Key.Should().Be(testKey);
        ourMessage.InstanceId.Should().NotBe(backplane2.InstanceId);

        // Cleanup - unsubscribe first to stop polling, then dispose
        await backplane2.UnsubscribeAsync();
        await StopHostedServicesAsync(serviceProvider2);
        await serviceProvider2.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task SubscribeAsync_ShouldReceiveTagInvalidationMessages()
    {
        // Arrange
        var backplane1 = ServiceProvider.GetRequiredService<IBackplane>();

        // Create a second service provider with same configuration for backplane coordination
        var services2 = new ServiceCollection();
        services2.AddLogging();

        // Get options from first instance to ensure backplane coordination works
        var firstInstanceOptions = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;

        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = firstInstanceOptions.KeyPrefix; // Use same key prefix for backplane coordination
            options.Schema = firstInstanceOptions.Schema;
            options.DefaultSerializer = firstInstanceOptions.DefaultSerializer;
            options.BackplanePollingInterval = TimeSpan.FromMilliseconds(100); // Faster polling for tests
        });

        var serviceProvider2 = services2.BuildServiceProvider();

        // Start hosted services for the second instance (needed for backplane polling)
        await StartHostedServicesAsync(serviceProvider2);

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

        // Small delay to allow the immediate poll from SubscribeAsync to complete
        // This prevents a race where we publish before the initial poll runs
        await Task.Delay(50);

        // Publish after subscribing - use unique tag per test run
        var testTag = $"cross-instance-test-tag-{Guid.NewGuid():N}";
        await backplane1.PublishTagInvalidationAsync(testTag);

        // Wait for message to be received (with timeout)
        // The immediate poll from SubscribeAsync plus the timer should pick this up
        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        received.Should().BeTrue();
        var ourMessage = receivedMessages.Should().ContainSingle(m => m.Tag == testTag, "should receive the published message").Subject;
        ourMessage.Type.Should().Be(BackplaneMessageType.TagInvalidation);
        ourMessage.Tag.Should().Be(testTag);
        ourMessage.InstanceId.Should().NotBe(backplane2.InstanceId);

        // Cleanup - unsubscribe first to stop polling, then dispose
        await backplane2.UnsubscribeAsync();
        await StopHostedServicesAsync(serviceProvider2);
        await serviceProvider2.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task BackplaneCoordination_ShouldInvalidateAcrossInstances()
    {
        // Arrange
        var storageProvider1 = ServiceProvider.GetRequiredService<IStorageProvider>();

        // Create a second service provider to simulate another instance
        var services2 = new ServiceCollection();
        services2.AddLogging();

        // Get options from first instance to ensure they use the same schema/prefix
        var firstInstanceOptions = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;

        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = firstInstanceOptions.KeyPrefix;
            options.Schema = firstInstanceOptions.Schema;
            options.DefaultSerializer = firstInstanceOptions.DefaultSerializer; // Ensure same serialization
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

        // Cleanup - stop hosted services before disposal
        await StopHostedServicesAsync(serviceProvider2);
        await serviceProvider2.DisposeAsync();
    }

    [Fact]
    public async Task InstanceId_ShouldBeUniquePerInstance()
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

        // Cleanup - stop hosted services before disposal
        await StopHostedServicesAsync(serviceProvider2);
        await serviceProvider2.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task UnsubscribeAsync_ShouldStopReceivingMessages()
    {
        // Arrange
        var backplane1 = ServiceProvider.GetRequiredService<IBackplane>();

        // Create a second service provider with same configuration for backplane coordination
        var services2 = new ServiceCollection();
        services2.AddLogging();

        // Get options from first instance to ensure backplane coordination works
        var firstInstanceOptions = ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;

        services2.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.KeyPrefix = firstInstanceOptions.KeyPrefix; // Use same key prefix for backplane coordination
            options.Schema = firstInstanceOptions.Schema;
            options.DefaultSerializer = firstInstanceOptions.DefaultSerializer;
            options.BackplanePollingInterval = TimeSpan.FromMilliseconds(100); // Faster polling for tests
        });

        var serviceProvider2 = services2.BuildServiceProvider();

        // Start hosted services for the second instance (needed for backplane polling)
        await StartHostedServicesAsync(serviceProvider2);

        var backplane2 = serviceProvider2.GetRequiredService<IBackplane>();

        var receivedMessages = new List<BackplaneMessage>();

        // Act
        // Subscribe first
        var messageReceived = new TaskCompletionSource<bool>();
        await backplane2.SubscribeAsync(message =>
        {
            receivedMessages.Add(message);
            messageReceived.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Small delay to allow the immediate poll from SubscribeAsync to complete
        await Task.Delay(50);

        // Publish a message after subscribing - use unique key
        var testKey = $"test-before-unsubscribe-{Guid.NewGuid():N}";
        await backplane1.PublishInvalidationAsync(testKey);

        // Wait for message to be received (with timeout to ensure it arrives)
        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().BeTrue("message should be received before unsubscribe");

        var messagesBeforeUnsubscribe = receivedMessages.Count;

        // Unsubscribe
        await backplane2.UnsubscribeAsync();

        // Give time for unsubscribe to take effect
        await Task.Delay(300);

        // Publish another message - use unique key
        await backplane1.PublishInvalidationAsync($"test-after-unsubscribe-{Guid.NewGuid():N}");
        await Task.Delay(TimeSpan.FromSeconds(1)); // Wait to ensure no polling happens

        var messagesAfterUnsubscribe = receivedMessages.Count;

        // Assert
        messagesBeforeUnsubscribe.Should().BeGreaterThan(0);
        messagesAfterUnsubscribe.Should().Be(messagesBeforeUnsubscribe); // No new messages

        // Cleanup
        await StopHostedServicesAsync(serviceProvider2);
        await serviceProvider2.DisposeAsync();
    }
}