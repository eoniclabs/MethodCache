using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.HybridCache.Extensions;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Compression;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.HealthChecks;
using MethodCache.Providers.Redis.Infrastructure;
using MethodCache.Providers.Redis.Services;
using Testcontainers.Redis;
using Xunit;
using DotNet.Testcontainers.Builders;
using StackExchange.Redis;

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
        
        // Use a modified Redis Infrastructure setup that avoids eager connection
        services.AddRedisInfrastructureForTests(options =>
        {
            options.ConnectionString = RedisConnectionString;
            options.EnableDistributedLocking = true;
            options.EnablePubSubInvalidation = true;
            // Unique prefix per test class instance to avoid cross-test collisions
            options.KeyPrefix = $"test:{Guid.NewGuid():N}:";
        });

        // Register infrastructure-based cache manager
        services.AddSingleton<ICacheManager>(provider =>
        {
            var storageProvider = provider.GetRequiredService<IStorageProvider>();
            var keyGenerator = provider.GetService<ICacheKeyGenerator>() ?? new DefaultCacheKeyGenerator();
            return new StorageProviderCacheManager(storageProvider, keyGenerator);
        });
        services.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();

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

/// <summary>
/// Test-specific Redis Infrastructure extensions that avoid connection issues during DI setup
/// </summary>
internal static class RedisInfrastructureTestExtensions
{
    public static IServiceCollection AddRedisInfrastructureForTests(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null)
    {
        // Add core infrastructure without the connection multiplexer that causes issues
        services.AddCacheInfrastructure();

        // Configure Redis options
        if (configureRedis != null)
        {
            services.Configure(configureRedis);
        }

        // Register Redis connection services - but make connection multiplexer lazy
        services.AddSingleton<RedisConnectionService>();
        services.AddHostedService<RedisConnectionService>(provider =>
            provider.GetRequiredService<RedisConnectionService>());

        // Register connection multiplexer with lazy initialization
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
            try
            {
                return ConnectionMultiplexer.Connect(options.ConnectionString);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Redis connection failed during test initialization", ex);
            }
        });

        // Register Redis-specific services
        services.AddSingleton<IRedisConnectionManager, RedisConnectionManager>();
        services.AddSingleton<IRedisSerializerFactory, RedisSerializerFactory>();
        services.AddSingleton<IRedisCompressionFactory, RedisCompressionFactory>();
        services.AddSingleton<IRedisSerializer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
            var serializerFactory = provider.GetRequiredService<IRedisSerializerFactory>();
            var compressionFactory = provider.GetRequiredService<IRedisCompressionFactory>();
            var logger = provider.GetRequiredService<ILogger<CompressedRedisSerializer>>();

            // Create base serializer
            var baseSerializer = serializerFactory.Create(options.DefaultSerializer);

            // Wrap with compression if enabled
            if (options.Compression != RedisCompressionType.None)
            {
                var compressor = compressionFactory.Create(options.Compression, options.CompressionThreshold);
                return new CompressedRedisSerializer(baseSerializer, compressor, logger);
            }

            return baseSerializer;
        });
        services.AddSingleton<IRedisTagManager, RedisTagManager>();

        // Register Redis infrastructure components - using TryAdd to avoid conflicts
        services.TryAddSingleton<IStorageProvider, RedisStorageProvider>();
        services.TryAddSingleton<IBackplane, RedisBackplane>();

        // Register core cache services that may be needed
        services.TryAddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();

        return services;
    }

    public static IServiceCollection AddRedisInfrastructureWithHealthChecksForTests(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null,
        string healthCheckName = "redis_infrastructure")
    {
        services.AddRedisInfrastructureForTests(configureRedis);

        // Add health checks
        services.AddHealthChecks()
            .AddCheck<MethodCache.Providers.Redis.HealthChecks.RedisInfrastructureHealthCheck>(healthCheckName);
        services.AddSingleton<MethodCache.Providers.Redis.HealthChecks.RedisInfrastructureHealthCheck>();

        return services;
    }

    public static IServiceCollection AddRedisHybridInfrastructureForTests(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null,
        Action<StorageOptions>? configureStorage = null)
    {
        // Add Redis infrastructure
        services.AddRedisInfrastructureForTests(configureRedis);

        // Add hybrid storage manager
        services.AddHybridStorageManager();

        return services;
    }
}

/// <summary>
/// Simple cache manager adapter that uses IStorageProvider for Infrastructure-based tests.
/// </summary>
internal class StorageProviderCacheManager : ICacheManager
{
    private readonly IStorageProvider _storageProvider;
    private readonly ICacheKeyGenerator _keyGenerator;

    public StorageProviderCacheManager(IStorageProvider storageProvider, ICacheKeyGenerator keyGenerator)
    {
        _storageProvider = storageProvider;
        _keyGenerator = keyGenerator;
    }

    public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool isIdempotent)
    {
        var key = keyGenerator.GenerateKey(methodName, args, settings);

        var cached = await _storageProvider.GetAsync<T>(key);
        if (cached != null)
        {
            return cached;
        }

        var value = await factory();
        if (value != null)
        {
            var expiration = settings.Duration ?? TimeSpan.FromMinutes(15);
            await _storageProvider.SetAsync(key, value, expiration, settings.Tags ?? new List<string>());
        }

        return value;
    }

    public Task InvalidateByTagsAsync(params string[] tags)
    {
        return Task.WhenAll(tags.Select(tag => _storageProvider.RemoveByTagAsync(tag)));
    }

    public Task InvalidateAsync(string methodName, params object[] args)
    {
        var key = _keyGenerator.GenerateKey(methodName, args, new CacheMethodSettings());
        return _storageProvider.RemoveAsync(key);
    }

    public Task InvalidateByKeysAsync(params string[] keys)
    {
        return Task.WhenAll(keys.Select(key => _storageProvider.RemoveAsync(key)));
    }

    public Task InvalidateByTagPatternAsync(string pattern)
    {
        // Basic implementation - remove by single tag
        return _storageProvider.RemoveByTagAsync(pattern);
    }

    public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
    {
        var key = keyGenerator.GenerateKey(methodName, args, settings);
        return await _storageProvider.GetAsync<T>(key);
    }

    public Task ClearAsync()
    {
        // Not supported by IStorageProvider interface
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_storageProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Lazy Redis cache manager that initializes Redis connection on first use.
/// Avoids DI container construction issues with Redis connectivity.
/// </summary>
internal class LazyRedisCacheManager : ICacheManager
{
    private readonly RedisOptions _options;
    private readonly ILogger? _logger;
    private readonly Lazy<ICacheManager> _lazyCacheManager;

    public LazyRedisCacheManager(RedisOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        _lazyCacheManager = new Lazy<ICacheManager>(CreateRedisCacheManager);
    }

    private ICacheManager CreateRedisCacheManager()
    {
        try
        {
            // Create a Redis connection multiplexer
            var connectionMultiplexer = ConnectionMultiplexer.Connect(_options.ConnectionString);

            // Create a simple Redis-based cache manager for tests
            return new SimpleRedisCacheManager(connectionMultiplexer, _options, _logger);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create Redis cache manager");
            throw;
        }
    }

    // Delegate all ICacheManager calls to the lazy instance
    public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool isIdempotent)
        => _lazyCacheManager.Value.GetOrCreateAsync(methodName, args, factory, settings, keyGenerator, isIdempotent);

    public Task InvalidateByTagsAsync(params string[] tags)
        => _lazyCacheManager.Value.InvalidateByTagsAsync(tags);

    public Task InvalidateByKeysAsync(params string[] keys)
        => _lazyCacheManager.Value.InvalidateByKeysAsync(keys);

    public Task InvalidateByTagPatternAsync(string pattern)
        => _lazyCacheManager.Value.InvalidateByTagPatternAsync(pattern);

    public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
        => _lazyCacheManager.Value.TryGetAsync<T>(methodName, args, settings, keyGenerator);
}

/// <summary>
/// Simple Redis cache manager for testing that directly uses Redis without complex DI.
/// </summary>
internal class SimpleRedisCacheManager : ICacheManager, IDisposable
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly IDatabase _database;
    private readonly RedisOptions _options;
    private readonly ILogger? _logger;
    private readonly DefaultCacheKeyGenerator _keyGenerator;

    public SimpleRedisCacheManager(IConnectionMultiplexer connectionMultiplexer, RedisOptions options, ILogger? logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _database = connectionMultiplexer.GetDatabase();
        _options = options;
        _logger = logger;
        _keyGenerator = new DefaultCacheKeyGenerator();
    }

    public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool isIdempotent)
    {
        var key = keyGenerator.GenerateKey(methodName, args, settings);
        var redisKey = $"{_options.KeyPrefix}{key}";

        try
        {
            var cachedValue = await _database.StringGetAsync(redisKey);
            if (cachedValue.HasValue)
            {
                var deserialized = System.Text.Json.JsonSerializer.Deserialize<T>(cachedValue!);
                if (deserialized != null)
                    return deserialized;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get cached value for key {Key}", redisKey);
        }

        var value = await factory();
        if (value != null)
        {
            try
            {
                var serialized = System.Text.Json.JsonSerializer.Serialize(value);
                var expiration = settings.Duration ?? TimeSpan.FromMinutes(15);
                await _database.StringSetAsync(redisKey, serialized, expiration);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to cache value for key {Key}", redisKey);
            }
        }

        return value;
    }

    public async Task InvalidateByTagsAsync(params string[] tags)
    {
        // Simple implementation - for tests, we'll just remove all keys with tag prefix
        foreach (var tag in tags)
        {
            var tagPattern = $"{_options.KeyPrefix}tag:{tag}:*";
            await InvalidateByPatternAsync(tagPattern);
        }
    }

    public Task InvalidateByKeysAsync(params string[] keys)
    {
        var redisKeys = keys.Select(k => (RedisKey)$"{_options.KeyPrefix}{k}").ToArray();
        return _database.KeyDeleteAsync(redisKeys);
    }

    public Task InvalidateByTagPatternAsync(string pattern)
    {
        var redisPattern = $"{_options.KeyPrefix}{pattern}";
        return InvalidateByPatternAsync(redisPattern);
    }

    public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
    {
        var key = keyGenerator.GenerateKey(methodName, args, settings);
        var redisKey = $"{_options.KeyPrefix}{key}";

        try
        {
            var cachedValue = await _database.StringGetAsync(redisKey);
            if (cachedValue.HasValue)
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(cachedValue!);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get cached value for key {Key}", redisKey);
        }

        return default;
    }

    private async Task InvalidateByPatternAsync(string pattern)
    {
        try
        {
            var server = _connectionMultiplexer.GetServer(_connectionMultiplexer.GetEndPoints().First());
            var keys = server.Keys(pattern: pattern);

            var keyArray = keys.ToArray();
            if (keyArray.Length > 0)
            {
                await _database.KeyDeleteAsync(keyArray);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to invalidate keys by pattern {Pattern}", pattern);
        }
    }

    public void Dispose()
    {
        _connectionMultiplexer?.Dispose();
    }
}
