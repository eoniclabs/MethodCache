using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Storage;
using MethodCache.Providers.SqlServer.Infrastructure;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public class SqlServerHybridCacheIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HybridStorageManager_ShouldUseL1AndL2Storage()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.Schema = $"hybrid_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();
        var key = "hybrid-test-key";
        var value = "hybrid-test-value";

        // Act
        await hybridStorage.SetAsync(key, value, TimeSpan.FromMinutes(10));
        var retrieved = await hybridStorage.GetAsync<string>(key);

        // Assert
        retrieved.Should().Be(value);

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HybridStorageManager_L1Miss_ShouldFallbackToL2()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.Schema = $"l1miss_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();
        var sqlServerStorage = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Infrastructure.SqlServerPersistentStorageProvider>();
        var key = "l1-miss-test";
        var value = "l1-miss-value";

        // Act
        // Store directly in L2 (SQL Server) to simulate L1 miss
        await sqlServerStorage.SetAsync(key, value, TimeSpan.FromMinutes(10));

        // Retrieve through hybrid storage (should hit L2 and populate L1)
        var retrieved = await hybridStorage.GetAsync<string>(key);

        // Assert
        retrieved.Should().Be(value);

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HybridStorageManager_WithTags_ShouldInvalidateAcrossBothLayers()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.Schema = $"taghybrid_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();
        var key = "tag-hybrid-test";
        var value = "tag-hybrid-value";
        var tag = "hybrid-tag";

        // Act
        // Store with tag
        await hybridStorage.SetAsync(key, value, TimeSpan.FromMinutes(10), new[] { tag });

        // Verify it's stored
        var beforeInvalidation = await hybridStorage.GetAsync<string>(key);
        beforeInvalidation.Should().Be(value);

        // Invalidate by tag
        await hybridStorage.RemoveByTagAsync(tag);

        // Verify it's removed from both layers
        var afterInvalidation = await hybridStorage.GetAsync<string>(key);
        afterInvalidation.Should().BeNull();

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HybridStorageManager_SingleInstance_RemoveByTag_ShouldWork()
    {
        // Debug test to isolate the issue without backplane complexity
        var services = new ServiceCollection();
        services.AddLogging();
        var schema = $"debug_{Guid.NewGuid():N}".Replace("-", "");
        services.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.EnableBackplane = false; // Disable backplane
            options.Schema = schema;
            options.KeyPrefix = "debug:";
        }, configureStorage: storageOptions =>
        {
            storageOptions.EnableAsyncL2Writes = false;
            storageOptions.L3Enabled = true; // Enable L3 for testing
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();

        var key = "debug-test-key";
        var value = "debug-test-value";
        var tag = "debug-test-tag";

        // Store value
        await hybridStorage.SetAsync(key, value, TimeSpan.FromMinutes(10), new[] { tag });

        // Verify it's stored
        var retrieved = await hybridStorage.GetAsync<string>(key);
        retrieved.Should().Be(value);

        // Remove by tag
        await hybridStorage.RemoveByTagAsync(tag);

        // Verify it's removed
        var afterRemoval = await hybridStorage.GetAsync<string>(key);
        afterRemoval.Should().BeNull("Value should be removed after RemoveByTagAsync");

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HybridStorageManager_WithBackplane_ShouldPropagateInvalidations()
    {
        // First run the debug test to ensure basic functionality works
        await HybridStorageManager_SingleInstance_RemoveByTag_ShouldWork();

        // Arrange
        var services1 = new ServiceCollection();
        services1.AddLogging();
        var schema = $"backplane_{Guid.NewGuid():N}".Replace("-", "");
        services1.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.EnableBackplane = true;
            options.Schema = schema;
            options.KeyPrefix = "shared:";
        }, configureStorage: storageOptions =>
        {
            storageOptions.EnableAsyncL2Writes = false; // Use synchronous L2 writes for testing
            storageOptions.L3Enabled = false; // Use L2 only, not L3
        });

        var serviceProvider1 = services1.BuildServiceProvider();

        // Initialize tables
        var tableManager1 = serviceProvider1.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager1.EnsureTablesExistAsync();

        // Create second instance
        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.EnableBackplane = true;
            options.Schema = schema;
            options.KeyPrefix = "shared:";
        }, configureStorage: storageOptions =>
        {
            storageOptions.EnableAsyncL2Writes = false; // Use synchronous L2 writes for testing
            storageOptions.L3Enabled = false; // Use L2 only, not L3
        });

        var serviceProvider2 = services2.BuildServiceProvider();

        var hybridStorage1 = serviceProvider1.GetRequiredService<HybridStorageManager>();
        var hybridStorage2 = serviceProvider2.GetRequiredService<HybridStorageManager>();

        var key = "backplane-hybrid-test";
        var value = "backplane-hybrid-value";
        var tag = "backplane-hybrid-tag";

        // Act
        // Store in instance 1
        await hybridStorage1.SetAsync(key, value, TimeSpan.FromMinutes(10), new[] { tag });

        // Retrieve from both instances to populate their L1 caches
        var value1 = await hybridStorage1.GetAsync<string>(key);
        var value2 = await hybridStorage2.GetAsync<string>(key);

        value1.Should().Be(value);
        value2.Should().Be(value);

        // Invalidate by tag from instance 2
        await hybridStorage2.RemoveByTagAsync(tag);

        // Check what layers are being used - get L1 storage directly to verify it's cleared
        var memoryStorage1 = serviceProvider1.GetRequiredService<IMemoryStorage>();
        var memoryStorage2 = serviceProvider2.GetRequiredService<IMemoryStorage>();

        // Verify that instances have different memory storage
        ReferenceEquals(memoryStorage1, memoryStorage2).Should().BeFalse("Each instance should have its own L1 memory storage");

        var l1Value = await memoryStorage2.GetAsync<string>(key);
        l1Value.Should().BeNull("L1 cache should be cleared after RemoveByTagAsync");

        // Check L2 storage directly
        var sqlStorage2 = serviceProvider2.GetRequiredService<SqlServerPersistentStorageProvider>();
        var l2Value = await sqlStorage2.GetAsync<string>(key);
        l2Value.Should().BeNull("L2 storage should be cleared after RemoveByTagAsync");

        // Verify instance 2 can't find it immediately (should be removed from its own storage)
        var immediate2 = await hybridStorage2.GetAsync<string>(key);
        immediate2.Should().BeNull("Instance 2 should not find value after its own RemoveByTagAsync");

        // Wait for backplane propagation
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Verify invalidation propagated to both instances
        var afterInvalidation1 = await hybridStorage1.GetAsync<string>(key);
        var afterInvalidation2 = await hybridStorage2.GetAsync<string>(key);

        // Assert
        afterInvalidation1.Should().BeNull("Instance 1 should not find value after backplane propagation");
        afterInvalidation2.Should().BeNull("Instance 2 should not find value after its own RemoveByTagAsync");

        // Cleanup
        await serviceProvider1.DisposeAsync();
        await serviceProvider2.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HybridStorageManager_Performance_ShouldBeReasonablyFast()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.Schema = $"perf_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();

        // Act & Assert
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Store 100 items
        for (int i = 0; i < 100; i++)
        {
            await hybridStorage.SetAsync($"perf-key-{i}", $"perf-value-{i}", TimeSpan.FromMinutes(10));
        }

        stopwatch.Stop();
        var storeTime = stopwatch.Elapsed;

        stopwatch.Restart();

        // Retrieve 100 items (should hit L1 cache mostly)
        for (int i = 0; i < 100; i++)
        {
            var value = await hybridStorage.GetAsync<string>($"perf-key-{i}");
            value.Should().Be($"perf-value-{i}");
        }

        stopwatch.Stop();
        var retrieveTime = stopwatch.Elapsed;

        // Performance assertions (these are loose - just to catch major regressions)
        storeTime.Should().BeLessThan(TimeSpan.FromSeconds(30), "storing 100 items should be reasonably fast");
        retrieveTime.Should().BeLessThan(TimeSpan.FromSeconds(5), "retrieving 100 items from L1 should be fast");

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HybridStorageManager_WithExpiration_ShouldRespectExpiration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerHybridInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.Schema = $"expiry_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();
        var key = "expiry-test";
        var value = "expiry-value";

        // Act
        await hybridStorage.SetAsync(key, value, TimeSpan.FromSeconds(2));

        // Verify it's stored
        var beforeExpiry = await hybridStorage.GetAsync<string>(key);
        beforeExpiry.Should().Be(value);

        // Wait for expiration
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Verify it's expired
        var afterExpiry = await hybridStorage.GetAsync<string>(key);
        afterExpiry.Should().BeNull();

        // Cleanup
        await serviceProvider.DisposeAsync();
    }
}