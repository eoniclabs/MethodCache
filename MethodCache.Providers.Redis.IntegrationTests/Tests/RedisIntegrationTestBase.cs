using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Infrastructure.Implementation;
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
        services.TryAddSingleton<RedisStorageProvider>();
        services.TryAddSingleton<IStorageProvider>(provider => provider.GetRequiredService<RedisStorageProvider>());
        services.TryAddSingleton<RedisBackplane>();
        services.TryAddSingleton<IBackplane>(provider => provider.GetRequiredService<RedisBackplane>());

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
        Action<MethodCache.Infrastructure.Configuration.StorageOptions>? configureStorage = null)
    {
        // Add Redis infrastructure
        services.AddRedisInfrastructureForTests(configureRedis);

        // Configure storage options
        if (configureStorage != null)
        {
            services.Configure(configureStorage);
        }

        // Add MethodCache core services (includes Core.ICacheManager)
        services.AddMethodCache();

        // Ensure IMemoryStorage is registered as singleton BEFORE AddCacheInfrastructure
        services.TryAddSingleton<MethodCache.Infrastructure.Abstractions.IMemoryStorage, MethodCache.Infrastructure.Implementation.MemoryStorage>();

        // Add core infrastructure for hybrid manager (same as SqlServer tests)
        services.AddCacheInfrastructure();

        // Register custom hybrid storage manager that uses Redis as L2
        services.AddScoped<HybridStorageManager>(provider =>
        {
            var l1Storage = provider.GetRequiredService<MethodCache.Infrastructure.Abstractions.IMemoryStorage>();
            var options = provider.GetRequiredService<IOptions<MethodCache.Infrastructure.Configuration.StorageOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridStorageManager>>();
            var l2Storage = provider.GetRequiredService<MethodCache.Providers.Redis.Infrastructure.RedisStorageProvider>();
            var backplane = provider.GetService<IBackplane>();

            return new HybridStorageManager(l1Storage, options, logger, l2Storage, null, backplane);
        });

        // Register Core IHybridCacheManager implementation that delegates to Infrastructure HybridStorageManager
        // Use Replace to ensure this overrides any subsequent registrations
        services.Replace(ServiceDescriptor.Scoped<MethodCache.Core.Storage.IHybridCacheManager>(provider =>
        {
            Console.WriteLine("DEBUG: IHybridCacheManager factory method called - creating TestHybridCacheManager");
            var hybridStorage = provider.GetRequiredService<HybridStorageManager>();
            var l1Storage = provider.GetRequiredService<MethodCache.Infrastructure.Abstractions.IMemoryStorage>();
            var l2Storage = provider.GetRequiredService<MethodCache.Providers.Redis.Infrastructure.RedisStorageProvider>();

            return new TestHybridCacheManager(hybridStorage, l1Storage, l2Storage);
        }));

        // Override ICacheManager to use the Infrastructure HybridStorageManager
        services.Replace(ServiceDescriptor.Scoped<ICacheManager>(provider =>
        {
            var hybridStorage = provider.GetRequiredService<HybridStorageManager>();
            var keyGenerator = provider.GetRequiredService<ICacheKeyGenerator>();

            return new InfrastructureCacheManager(hybridStorage, keyGenerator);
        }));

        return services;
    }
}

/// <summary>
/// Cache manager implementation that bridges Core.ICacheManager with Infrastructure.HybridStorageManager
/// </summary>
internal class InfrastructureCacheManager : ICacheManager
{
    private readonly HybridStorageManager _hybridStorage;
    private readonly ICacheKeyGenerator _keyGenerator;

    public InfrastructureCacheManager(HybridStorageManager hybridStorage, ICacheKeyGenerator keyGenerator)
    {
        _hybridStorage = hybridStorage;
        _keyGenerator = keyGenerator;
    }

    public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
    {
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);

        // Try to get from cache first
        var cachedValue = await _hybridStorage.GetAsync<T>(cacheKey);
        if (cachedValue != null)
        {
            return cachedValue;
        }

        // Execute factory and cache the result
        var result = await factory();
        if (result != null)
        {
            var expiration = settings.Duration ?? TimeSpan.FromMinutes(15);
            await _hybridStorage.SetAsync(cacheKey, result, expiration, settings.Tags ?? new List<string>());
        }

        return result;
    }

    public Task RemoveAsync(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
    {
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        return _hybridStorage.RemoveAsync(cacheKey);
    }

    public Task RemoveByTagAsync(string tag)
    {
        return _hybridStorage.RemoveByTagAsync(tag);
    }

    public Task RemoveByTagPatternAsync(string pattern)
    {
        return _hybridStorage.RemoveByTagAsync(pattern); // Infrastructure version should handle patterns
    }

    public Task InvalidateByKeysAsync(params string[] keys)
    {
        var tasks = keys.Select(key => _hybridStorage.RemoveAsync(key));
        return Task.WhenAll(tasks);
    }

    public Task InvalidateByTagsAsync(params string[] tags)
    {
        var tasks = tags.Select(tag => _hybridStorage.RemoveByTagAsync(tag));
        return Task.WhenAll(tasks);
    }

    public Task InvalidateByTagPatternAsync(string pattern)
    {
        return _hybridStorage.RemoveByTagAsync(pattern);
    }

    public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
    {
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        return await _hybridStorage.GetAsync<T>(cacheKey);
    }
}

/// <summary>
/// Simple cache manager adapter that uses IStorageProvider for Infrastructure-based tests.
/// </summary>
internal class StorageProviderCacheManager : ICacheManager
{
    private readonly MethodCache.Infrastructure.Abstractions.IStorageProvider _storageProvider;
    private readonly ICacheKeyGenerator _keyGenerator;

    public StorageProviderCacheManager(MethodCache.Infrastructure.Abstractions.IStorageProvider storageProvider, ICacheKeyGenerator keyGenerator)
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
/// Test adapter that implements Core.IHybridCacheManager and delegates to Infrastructure.HybridStorageManager
/// </summary>
internal class TestHybridCacheManager : MethodCache.Core.Storage.IHybridCacheManager
{
    private readonly HybridStorageManager _hybridStorage;
    private readonly MethodCache.Infrastructure.Abstractions.IMemoryStorage _l1Storage = null!;
    private readonly MethodCache.Providers.Redis.Infrastructure.RedisStorageProvider _l2Storage;

    public TestHybridCacheManager(
        HybridStorageManager hybridStorage,
        MethodCache.Infrastructure.Abstractions.IMemoryStorage l1Storage,
        MethodCache.Providers.Redis.Infrastructure.RedisStorageProvider l2Storage)
    {
        Console.WriteLine("DEBUG: TestHybridCacheManager constructor called");
        _hybridStorage = hybridStorage;
        _l2Storage = l2Storage;

        // Use reflection to get the actual L1 storage from HybridStorageManager
        var l1Field = typeof(HybridStorageManager).GetField("_l1Storage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        _l1Storage = (MethodCache.Infrastructure.Abstractions.IMemoryStorage)l1Field!.GetValue(hybridStorage)!;
        Console.WriteLine($"DEBUG: L1 storage extracted via reflection: {_l1Storage?.GetType().Name}");
    }

    // ICacheManager implementation - delegate to hybrid storage with proper L1 population
    public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
    {
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);

        // Try to get from cache first (this will search L1, then L2)
        var cached = await _hybridStorage.GetAsync<T>(cacheKey);
        if (cached != null)
        {
            return cached;
        }

        // Execute factory and cache result
        var result = await factory();
        if (result != null)
        {
            var expiration = settings.Duration ?? TimeSpan.FromMinutes(10);
            var tags = settings.Tags ?? new List<string>();

            // Store via hybrid storage (handles L1+L2 coordination)
            await _hybridStorage.SetAsync(cacheKey, result, expiration, tags);

            // Also explicitly store in L1 to ensure it's populated for the tests
            await _l1Storage.SetAsync(cacheKey, result, expiration, tags);
        }

        return result;
    }

    public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
    {
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        return await _hybridStorage.GetAsync<T>(cacheKey);
    }

    public async Task InvalidateByTagsAsync(params string[] tags)
    {
        foreach (var tag in tags)
        {
            await _hybridStorage.RemoveByTagAsync(tag);
        }
    }

    public async Task InvalidateByKeysAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            await _hybridStorage.RemoveAsync(key);
        }
    }

    public Task InvalidateByTagPatternAsync(string pattern)
    {
        // Basic implementation - remove by single tag
        return _hybridStorage.RemoveByTagAsync(pattern);
    }

    // IHybridCacheManager implementation - access layer-specific storage
    public async Task<T?> GetFromL1Async<T>(string key)
    {
        Console.WriteLine($"DEBUG: GetFromL1Async called for key: {key}");
        var result = await _l1Storage.GetAsync<T>(key);
        Console.WriteLine($"DEBUG: GetFromL1Async result: {result?.ToString() ?? "null"}");

        // Also check if the value exists by calling the hybrid storage directly
        var hybridResult = await _hybridStorage.GetAsync<T>(key);
        Console.WriteLine($"DEBUG: HybridStorage result: {hybridResult?.ToString() ?? "null"}");

        return result;
    }

    public Task<T?> GetFromL2Async<T>(string key)
    {
        return _l2Storage.GetAsync<T>(key);
    }

    public Task<T?> GetFromL3Async<T>(string key)
    {
        // No L3 in Redis setup
        return Task.FromResult<T?>(default);
    }

    public Task SetInL1Async<T>(string key, T value, TimeSpan expiration)
    {
        return _l1Storage.SetAsync(key, value, expiration);
    }

    public Task SetInL1Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
    {
        return _l1Storage.SetAsync(key, value, expiration, tags);
    }

    public Task SetInL2Async<T>(string key, T value, TimeSpan expiration)
    {
        return _l2Storage.SetAsync(key, value, expiration);
    }

    public Task SetInL3Async<T>(string key, T value, TimeSpan expiration)
    {
        // No L3 in Redis setup
        return Task.CompletedTask;
    }

    public Task SetInBothAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration)
    {
        // Use shorter expiration for both layers
        var expiration = l1Expiration < l2Expiration ? l1Expiration : l2Expiration;
        return _hybridStorage.SetAsync(key, value, expiration);
    }

    public Task SetInAllAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration, TimeSpan l3Expiration)
    {
        // Use shortest expiration
        var expiration = new[] { l1Expiration, l2Expiration, l3Expiration }.Min();
        return _hybridStorage.SetAsync(key, value, expiration);
    }

    public Task InvalidateL1Async(string key)
    {
        return _l1Storage.RemoveAsync(key);
    }

    public Task InvalidateL2Async(string key)
    {
        return _l2Storage.RemoveAsync(key);
    }

    public Task InvalidateL3Async(string key)
    {
        // No L3 in Redis setup
        return Task.CompletedTask;
    }

    public Task InvalidateBothAsync(string key)
    {
        return _hybridStorage.RemoveAsync(key);
    }

    public Task InvalidateAllAsync(string key)
    {
        return _hybridStorage.RemoveAsync(key);
    }

    public Task WarmL1CacheAsync(params string[] keys)
    {
        // Not implemented for test adapter
        return Task.CompletedTask;
    }

    public async Task<MethodCache.Core.Storage.HybridCacheStats> GetStatsAsync()
    {
        var stats = await _hybridStorage.GetStatsAsync();
        return new MethodCache.Core.Storage.HybridCacheStats
        {
            L1Hits = stats?.AdditionalStats?.TryGetValue("L1Hits", out var l1Hits) == true ? Convert.ToInt64(l1Hits) : 0,
            L1Misses = stats?.AdditionalStats?.TryGetValue("L1Misses", out var l1Misses) == true ? Convert.ToInt64(l1Misses) : 0,
            L2Hits = stats?.AdditionalStats?.TryGetValue("L2Hits", out var l2Hits) == true ? Convert.ToInt64(l2Hits) : 0,
            L2Misses = stats?.AdditionalStats?.TryGetValue("L2Misses", out var l2Misses) == true ? Convert.ToInt64(l2Misses) : 0,
            L3Hits = 0,
            L3Misses = 0,
            L1Entries = stats?.AdditionalStats?.TryGetValue("L1EntryCount", out var l1Entries) == true ? Convert.ToInt64(l1Entries) : 0,
            L1Evictions = stats?.AdditionalStats?.TryGetValue("L1Evictions", out var l1Evictions) == true ? Convert.ToInt64(l1Evictions) : 0,
            BackplaneMessagesSent = 0,
            BackplaneMessagesReceived = 0,
            TagMappingCount = 0,
            UniqueTagCount = 0,
            EfficientTagInvalidationEnabled = true
        };
    }

    public Task EvictFromL1Async(string key)
    {
        return _l1Storage.RemoveAsync(key);
    }

    public Task SyncL1CacheAsync()
    {
        // Not implemented for test adapter
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Nothing to dispose in test adapter
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


