using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Providers.Redis.Extensions;
using Testcontainers.Redis;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public abstract class RedisIntegrationTestBase : IAsyncLifetime
{
    protected RedisContainer RedisContainer { get; private set; } = null!;
    protected IServiceProvider ServiceProvider { get; private set; } = null!;
    protected ICacheManager CacheManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        RedisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(6379, true)
            .Build();

        await RedisContainer.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.EnableDistributedLocking = true;
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = "test:";
        });

        ServiceProvider = services.BuildServiceProvider();
        CacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
    }

    public async Task DisposeAsync()
    {
        if (ServiceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (ServiceProvider is IDisposable disposable)
            disposable.Dispose();
        
        if (RedisContainer != null)
            await RedisContainer.DisposeAsync();
    }
}