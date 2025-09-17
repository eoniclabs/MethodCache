using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Providers.Redis.Extensions;
using Testcontainers.Redis;
using Xunit;
using DotNet.Testcontainers.Builders;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public abstract class RedisIntegrationTestBase : IAsyncLifetime
{
    protected RedisContainer RedisContainer { get; private set; } = null!;
    protected IServiceProvider ServiceProvider { get; private set; } = null!;
    protected ICacheManager CacheManager { get; private set; } = null!;
    protected string RedisConnectionString { get; private set; } = null!;

    // Share a single Redis container across all test classes to reduce startup cost
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static RedisContainer? SharedContainer;

    public async Task InitializeAsync()
    {
        await InitLock.WaitAsync();
        try
        {
            if (SharedContainer == null)
            {
                // If an external Redis is provided, do not create a container
                var external = Environment.GetEnvironmentVariable("METHODCACHE_REDIS_URL")
                               ?? Environment.GetEnvironmentVariable("REDIS_URL");
                if (string.IsNullOrWhiteSpace(external))
                {
                    try
                    {
                        SharedContainer = new RedisBuilder()
                            .WithImage("redis:7-alpine")
                            .WithPortBinding(6379, true)
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli ping"))
                            .WithReuse(true)
                            .Build();

                        // Start with a reasonable timeout to avoid hanging the test run
                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        await SharedContainer.StartAsync(cts.Token);
                    }
                    catch (Exception ex)
                    {
                        // If Docker is unavailable, fail initialization with clear guidance
                        throw new InvalidOperationException($"Docker not available for Redis integration tests. {ex.Message}\nSet METHODCACHE_REDIS_URL or REDIS_URL to run against an external Redis, or start Docker.");
                    }
                }
                else
                {
                    // Using external Redis, so no container is created
                    SharedContainer = null;
                }
            }
        }
        finally
        {
            InitLock.Release();
        }

        // Use the shared container for this test class if present
        RedisContainer = SharedContainer!;
        RedisConnectionString = SharedContainer != null
            ? SharedContainer.GetConnectionString()
            : (Environment.GetEnvironmentVariable("METHODCACHE_REDIS_URL")
               ?? Environment.GetEnvironmentVariable("REDIS_URL")
               ?? throw new InvalidOperationException("No Redis connection available."));

        var services = new ServiceCollection();
        // Reduce logging noise and overhead for CI speed
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        
        services.AddRedisCache(options =>
        {
            options.ConnectionString = RedisConnectionString;
            options.EnableDistributedLocking = true;
            options.EnablePubSubInvalidation = true;
            // Unique prefix per test class instance to avoid cross-test collisions
            options.KeyPrefix = $"test:{Guid.NewGuid():N}:";
        });

        ServiceProvider = services.BuildServiceProvider();

        await StartHostedServicesAsync(ServiceProvider);

        CacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
    }

    public async Task DisposeAsync()
    {
        // Stop hosted services first
        if (ServiceProvider != null)
        {
            await StopHostedServicesAsync(ServiceProvider);
            await DisposeServiceProviderAsync(ServiceProvider);
        }
        // Do not dispose the shared container here; keep it alive for the entire test run.
    }

    protected static async Task StartHostedServicesAsync(IServiceProvider serviceProvider)
    {
        var hostedServices = serviceProvider.GetServices<IHostedService>();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    protected static async Task StopHostedServicesAsync(IServiceProvider serviceProvider)
    {
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
    }

    protected static async Task DisposeServiceProviderAsync(IServiceProvider serviceProvider)
    {
        if (serviceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (serviceProvider is IDisposable disposable)
            disposable.Dispose();
    }

    protected static string CreateKeyPrefix(string prefix) => $"{prefix}:{Guid.NewGuid():N}:";
}
