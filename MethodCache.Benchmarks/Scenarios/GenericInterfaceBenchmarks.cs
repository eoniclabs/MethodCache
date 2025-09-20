using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Benchmarks testing generic interface performance
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class GenericInterfaceBenchmarks : BenchmarkBase
{
    private IGenericRepository<User> _userRepository = null!;
    private IGenericRepository<Product> _productRepository = null!;
    private IGenericService _genericService = null!;

    [Params(50, 100, 200)]
    public int ItemCount { get; set; }

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<IGenericRepository<User>, GenericRepository<User>>();
        services.AddSingleton<IGenericRepository<Product>, GenericRepository<Product>>();
        services.AddSingleton<IGenericService, GenericService>();
    }

    protected override void OnSetupComplete()
    {
        _userRepository = ServiceProvider.GetRequiredService<IGenericRepository<User>>();
        _productRepository = ServiceProvider.GetRequiredService<IGenericRepository<Product>>();
        _genericService = ServiceProvider.GetRequiredService<IGenericService>();
    }

    [Benchmark(Baseline = true)]
    public async Task<List<User>> GenericRepository_Users_CacheHits()
    {
        var results = new List<User>();
        
        // Warm up cache
        for (int i = 0; i < ItemCount; i++)
        {
            await _userRepository.GetByIdAsync(i);
        }
        
        // Measure cache hits
        for (int i = 0; i < ItemCount; i++)
        {
            results.Add(await _userRepository.GetByIdAsync(i));
        }
        
        return results;
    }

    [Benchmark]
    public async Task<List<Product>> GenericRepository_Products_CacheHits()
    {
        var results = new List<Product>();
        
        // Warm up cache
        for (int i = 0; i < ItemCount; i++)
        {
            await _productRepository.GetByIdAsync(i);
        }
        
        // Measure cache hits
        for (int i = 0; i < ItemCount; i++)
        {
            results.Add(await _productRepository.GetByIdAsync(i));
        }
        
        return results;
    }

    [Benchmark]
    public async Task<List<object>> GenericRepository_Mixed_Operations()
    {
        var results = new List<object>();
        
        for (int i = 0; i < ItemCount / 2; i++)
        {
            // Interleave user and product operations
            results.Add(await _userRepository.GetByIdAsync(i));
            results.Add(await _productRepository.GetByIdAsync(i));
            
            if (i % 10 == 0)
            {
                // Occasional list operations
                var users = await _userRepository.GetAllAsync();
                var products = await _productRepository.GetAllAsync();
                results.AddRange(users.Cast<object>());
                results.AddRange(products.Cast<object>());
            }
        }
        
        return results;
    }

    [Benchmark]
    public async Task GenericRepository_CacheInvalidation()
    {
        // Warm up both caches
        for (int i = 0; i < ItemCount; i++)
        {
            await _userRepository.GetByIdAsync(i);
            await _productRepository.GetByIdAsync(i);
        }
        
        // Test independent invalidation
        for (int i = 0; i < 10; i++)
        {
            await _userRepository.InvalidateAsync(i);
            await _productRepository.InvalidateAsync(i + 1000); // Different ID to test isolation
        }
    }

    [Benchmark]
    public async Task<List<object>> GenericMethods_WithConstraints()
    {
        var results = new List<object>();
        
        // Create test entities
        var users = Enumerable.Range(0, ItemCount).Select(i => User.Create(i)).ToList();
        var products = Enumerable.Range(0, ItemCount).Select(i => Product.Create(i)).ToList();
        
        // Test generic methods with constraints
        for (int i = 0; i < ItemCount / 10; i++)
        {
            var userBatch = users.Skip(i * 10).Take(10).ToList();
            var productBatch = products.Skip(i * 10).Take(10).ToList();
            
            // These will use generic method caching
            var processedUsers = await _genericService.ProcessEntitiesAsync(userBatch);
            var processedProducts = await _genericService.ProcessEntitiesAsync(productBatch);
            
            results.AddRange(processedUsers.Cast<object>());
            results.AddRange(processedProducts.Cast<object>());
        }
        
        return results;
    }

    [Benchmark]
    public async Task ConcurrentGenericAccess()
    {
        var tasks = new List<Task>();
        
        for (int thread = 0; thread < 4; thread++)
        {
            int threadId = thread;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < ItemCount / 4; i++)
                {
                    int id = threadId * (ItemCount / 4) + i;
                    
                    // Concurrent access to different generic instances
                    await _userRepository.GetByIdAsync(id);
                    await _productRepository.GetByIdAsync(id);
                }
            }));
        }
        
        await Task.WhenAll(tasks);
    }
}

// Generic repository interface
public interface IGenericRepository<T> where T : class
{
    Task<T> GetByIdAsync(int id);
    Task<List<T>> GetAllAsync();
    Task<List<T>> GetByIdsAsync(int[] ids);
    Task InvalidateAsync(int id);
}

// Generic repository implementation
public class GenericRepository<T> : IGenericRepository<T> where T : class
{
    private readonly ICacheManager _cacheManager;
    private readonly MethodCacheConfiguration _configuration;
    private readonly ICacheKeyGenerator _keyGenerator;

    public GenericRepository(
        ICacheManager cacheManager, 
        MethodCacheConfiguration configuration,
        ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _configuration = configuration;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:05:00", Tags = new[] { "entities" })]
    public virtual async Task<T> GetByIdAsync(int id)
    {
        var settings = _configuration.GetMethodSettings($"GenericRepository<{typeof(T).Name}>.GetByIdAsync");
        var args = new object[] { id };
        
        return await _cacheManager.GetOrCreateAsync<T>(
            $"GenericRepository<{typeof(T).Name}>.GetByIdAsync",
            args,
            async () => await CreateEntityAsync(id),
            settings,
            _keyGenerator,
            true);
    }

    [Cache(Duration = "00:02:00", Tags = new[] { "entities" })]
    public virtual async Task<List<T>> GetAllAsync()
    {
        var settings = _configuration.GetMethodSettings($"GenericRepository<{typeof(T).Name}>.GetAllAsync");
        var args = Array.Empty<object>();
        
        return await _cacheManager.GetOrCreateAsync<List<T>>(
            $"GenericRepository<{typeof(T).Name}>.GetAllAsync",
            args,
            async () => await CreateAllEntitiesAsync(),
            settings,
            _keyGenerator,
            true);
    }

    [Cache(Duration = "00:03:00", Tags = new[] { "entities" })]
    public virtual async Task<List<T>> GetByIdsAsync(int[] ids)
    {
        var settings = _configuration.GetMethodSettings($"GenericRepository<{typeof(T).Name}>.GetByIdsAsync");
        var args = new object[] { ids };
        
        return await _cacheManager.GetOrCreateAsync<List<T>>(
            $"GenericRepository<{typeof(T).Name}>.GetByIdsAsync",
            args,
            async () => await CreateEntitiesByIdsAsync(ids),
            settings,
            _keyGenerator,
            true);
    }

    [CacheInvalidate(Tags = new[] { "entities" })]
    public virtual async Task InvalidateAsync(int id)
    {
        await _cacheManager.InvalidateByTagsAsync("entities");
    }

    private async Task<T> CreateEntityAsync(int id)
    {
        await Task.Delay(Random.Shared.Next(5, 15));
        
        if (typeof(T) == typeof(User))
            return (T)(object)User.Create(id);
        if (typeof(T) == typeof(Product))
            return (T)(object)Product.Create(id);
            
        throw new NotSupportedException($"Type {typeof(T).Name} not supported");
    }

    private async Task<List<T>> CreateAllEntitiesAsync()
    {
        await Task.Delay(Random.Shared.Next(20, 50));
        
        return Enumerable.Range(1, 10)
            .Select(i => CreateEntityAsync(i).Result)
            .ToList();
    }

    private async Task<List<T>> CreateEntitiesByIdsAsync(int[] ids)
    {
        await Task.Delay(Random.Shared.Next(10, 30));
        
        return ids.Select(id => CreateEntityAsync(id).Result).ToList();
    }
}

// Generic service interface for testing generic methods
public interface IGenericService
{
    Task<List<T>> ProcessEntitiesAsync<T>(List<T> entities) where T : class;
    Task<TResult> TransformAsync<TInput, TResult>(TInput input) where TInput : class where TResult : class, new();
}

// Generic service implementation
public class GenericService : IGenericService
{
    private readonly ICacheManager _cacheManager;
    private readonly MethodCacheConfiguration _configuration;
    private readonly ICacheKeyGenerator _keyGenerator;

    public GenericService(
        ICacheManager cacheManager, 
        MethodCacheConfiguration configuration,
        ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _configuration = configuration;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:03:00", Tags = new[] { "processing" })]
    public virtual async Task<List<T>> ProcessEntitiesAsync<T>(List<T> entities) where T : class
    {
        var settings = _configuration.GetMethodSettings($"GenericService.ProcessEntitiesAsync<{typeof(T).Name}>");
        var args = new object[] { entities };
        
        return await _cacheManager.GetOrCreateAsync<List<T>>(
            $"GenericService.ProcessEntitiesAsync<{typeof(T).Name}>",
            args,
            async () => await DoProcessEntitiesAsync(entities),
            settings,
            _keyGenerator,
            true);
    }

    [Cache(Duration = "00:05:00", Tags = new[] { "transformation" })]
    public virtual async Task<TResult> TransformAsync<TInput, TResult>(TInput input) 
        where TInput : class 
        where TResult : class, new()
    {
        var settings = _configuration.GetMethodSettings($"GenericService.TransformAsync<{typeof(TInput).Name},{typeof(TResult).Name}>");
        var args = new object[] { input };
        
        return await _cacheManager.GetOrCreateAsync<TResult>(
            $"GenericService.TransformAsync<{typeof(TInput).Name},{typeof(TResult).Name}>",
            args,
            async () => await DoTransformAsync<TInput, TResult>(input),
            settings,
            _keyGenerator,
            true);
    }

    private async Task<List<T>> DoProcessEntitiesAsync<T>(List<T> entities) where T : class
    {
        await Task.Delay(entities.Count * 2); // Simulate processing time
        return entities; // Return processed entities
    }

    private async Task<TResult> DoTransformAsync<TInput, TResult>(TInput input) 
        where TInput : class 
        where TResult : class, new()
    {
        await Task.Delay(50); // Simulate transformation time
        return new TResult();
    }
}