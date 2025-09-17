using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Providers.Redis.Extensions;
using Testcontainers.Redis;
using Xunit;
using DotNet.Testcontainers.Builders;

namespace MethodCache.Providers.Redis.IntegrationTests.Fixtures;

/// <summary>
/// Shared Redis container fixture that creates a single Redis instance
/// for all integration tests to avoid Docker resource contention.
/// </summary>
public class SharedRedisFixture : IAsyncLifetime
{
    public RedisContainer RedisContainer { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Create Redis container with optimized settings for testing
        RedisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli ping"))
            .WithReuse(true)
            .WithStartupCallback((container, ct) => 
            {
                Console.WriteLine($"Redis container started: {container.GetConnectionString()}");
                return Task.CompletedTask;
            })
            .Build();

        // Start the container with timeout handling
        var startupTimeout = TimeSpan.FromMinutes(2);
        using var cts = new CancellationTokenSource(startupTimeout);
        
        try
        {
            await RedisContainer.StartAsync(cts.Token);
            ConnectionString = RedisContainer.GetConnectionString();
            Console.WriteLine($"Shared Redis container ready: {ConnectionString}");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Redis container failed to start within {startupTimeout.TotalSeconds} seconds");
        }
    }

    public async Task DisposeAsync()
    {
        if (RedisContainer != null)
        {
            Console.WriteLine("Disposing shared Redis container...");
            await RedisContainer.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a service provider with Redis cache configured to use the shared container.
    /// </summary>
    public async Task<IServiceProvider> CreateServiceProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => 
            builder.AddConsole().SetMinimumLevel(LogLevel.Warning)); // Reduced logging for cleaner output
        
        services.AddRedisCache(options =>
        {
            options.ConnectionString = ConnectionString;
            options.EnableDistributedLocking = true;
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = $"test:{Guid.NewGuid():N}:"; // Unique prefix per test instance
        });

        var serviceProvider = services.BuildServiceProvider();
        
        // Start hosted services to initialize Redis connection
        var hostedServices = serviceProvider.GetServices<IHostedService>();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
        
        return serviceProvider;
    }

    /// <summary>
    /// Properly disposes a service provider created by CreateServiceProviderAsync.
    /// </summary>
    public static async Task DisposeServiceProviderAsync(IServiceProvider serviceProvider)
    {
        // Stop hosted services first
        var hostedServices = serviceProvider.GetServices<IHostedService>();
        foreach (var hostedService in hostedServices)
        {
            try
            {
                await hostedService.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Ignore exceptions during cleanup
            }
        }
        
        if (serviceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (serviceProvider is IDisposable disposable)
            disposable.Dispose();
    }
}

/// <summary>
/// xUnit collection definition to share the Redis fixture across all tests.
/// </summary>
[CollectionDefinition("SharedRedis")]
public class SharedRedisCollection : ICollectionFixture<SharedRedisFixture>
{
}
