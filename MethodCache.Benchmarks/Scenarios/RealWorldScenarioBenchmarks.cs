using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using MethodCache.Abstractions.Registry;
using MethodCache.Benchmarks.Infrastructure;
using MethodCache.Core.Configuration.Surfaces.Attributes;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.KeyGeneration;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Benchmarks simulating real-world application scenarios
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class RealWorldScenarioBenchmarks : BenchmarkBase
{
    private IUserService _userService = null!;
    private IProductService _productService = null!;
    private IApiService _apiService = null!;

    [Params(50, 100, 200)]
    public int UserCount { get; set; }

    [Params(100, 500, 1000)]
    public int ProductCount { get; set; }

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IProductService, ProductService>();
        services.AddSingleton<IApiService, ApiService>();
    }

    protected override void OnSetupComplete()
    {
        _userService = ServiceProvider.GetRequiredService<IUserService>();
        _productService = ServiceProvider.GetRequiredService<IProductService>();
        _apiService = ServiceProvider.GetRequiredService<IApiService>();
    }

    [Benchmark(Baseline = true)]
    public async Task WebApplication_UserDashboard()
    {
        // Simulate loading a user dashboard
        var userIds = Enumerable.Range(1, UserCount).ToList();
        var tasks = new List<Task>();

        foreach (var userId in userIds)
        {
            tasks.Add(Task.Run(async () =>
            {
                // Get user profile
                var user = await _userService.GetUserAsync(userId);
                
                // Get user's recent activity (cache miss more likely)
                var activity = await _userService.GetUserActivityAsync(userId);
                
                // Get user preferences (likely cached)
                var preferences = await _userService.GetUserPreferencesAsync(userId);
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ECommerce_ProductCatalog()
    {
        // Simulate product catalog browsing
        var productIds = Enumerable.Range(1, ProductCount).ToList();
        var tasks = new List<Task>();

        foreach (var productId in productIds)
        {
            tasks.Add(Task.Run(async () =>
            {
                // Get product details (frequently cached)
                var product = await _productService.GetProductAsync(productId);
                
                // Get product inventory (less frequently cached)
                var inventory = await _productService.GetProductInventoryAsync(productId);
                
                // Get related products (might be cached)
                var related = await _productService.GetRelatedProductsAsync(productId);
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ApiGateway_ExternalCalls()
    {
        // Simulate API gateway caching external service calls
        var requestIds = Enumerable.Range(1, 100).ToList();
        var tasks = new List<Task>();

        foreach (var requestId in requestIds)
        {
            tasks.Add(Task.Run(async () =>
            {
                // Cache external API responses with different expiration times
                var weatherData = await _apiService.GetWeatherDataAsync(requestId % 10); // Few cities, high hit ratio
                var stockData = await _apiService.GetStockDataAsync($"STOCK{requestId % 20}"); // More stocks, medium hit ratio
                var newsData = await _apiService.GetNewsDataAsync(); // Same for all, highest hit ratio
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task MobileApp_OfflineFirst()
    {
        // Simulate mobile app with offline-first caching strategy
        var userIds = Enumerable.Range(1, UserCount).ToList();
        var tasks = new List<Task>();

        foreach (var userId in userIds)
        {
            tasks.Add(Task.Run(async () =>
            {
                // Cache user data for offline access (long expiration)
                var user = await _userService.GetUserForOfflineAsync(userId);
                
                // Cache recent messages (medium expiration)
                var messages = await _userService.GetRecentMessagesAsync(userId);
                
                // Cache app configuration (very long expiration)
                var config = await _apiService.GetAppConfigurationAsync();
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task RealtimeApp_FrequentUpdates()
    {
        // Simulate real-time application with frequent cache invalidations
        var iterations = 10;
        
        for (int i = 0; i < iterations; i++)
        {
            var batchTasks = new List<Task>();
            
            // Read operations
            for (int j = 0; j < 50; j++)
            {
                batchTasks.Add(_userService.GetUserActivityAsync(j % UserCount));
                batchTasks.Add(_productService.GetProductInventoryAsync(j % ProductCount));
            }
            
            // Update operations (cause invalidations)
            for (int j = 0; j < 5; j++)
            {
                batchTasks.Add(_userService.UpdateUserActivityAsync(j % UserCount));
                batchTasks.Add(_productService.UpdateProductInventoryAsync(j % ProductCount));
            }
            
            await Task.WhenAll(batchTasks);
        }
    }

    [Benchmark]
    public async Task Analytics_ReportGeneration()
    {
        // Simulate analytics dashboard with expensive computations
        var reportTasks = new List<Task>();
        
        // Generate various reports that could benefit from caching
        reportTasks.Add(_userService.GetUserStatisticsAsync());
        reportTasks.Add(_productService.GetProductAnalyticsAsync());
        reportTasks.Add(_apiService.GetSystemMetricsAsync());
        
        // Simulate multiple dashboard users requesting the same reports
        for (int i = 0; i < 5; i++)
        {
            reportTasks.Add(_userService.GetUserStatisticsAsync());
            reportTasks.Add(_productService.GetProductAnalyticsAsync());
        }
        
        await Task.WhenAll(reportTasks);
    }
}

// User Service Interface and Implementation
public interface IUserService
{
    Task<User> GetUserAsync(int userId);
    Task<List<string>> GetUserActivityAsync(int userId);
    Task<object> GetUserPreferencesAsync(int userId);
    Task<User> GetUserForOfflineAsync(int userId);
    Task<List<string>> GetRecentMessagesAsync(int userId);
    Task UpdateUserActivityAsync(int userId);
    Task<object> GetUserStatisticsAsync();
}

public class UserService : IUserService
{
    private readonly ICacheManager _cacheManager;
    private readonly IPolicyRegistry _policyRegistry;
    private readonly ICacheKeyGenerator _keyGenerator;

    public UserService(ICacheManager cacheManager, IPolicyRegistry policyRegistry, ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _policyRegistry = policyRegistry;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:05:00", Tags = new[] { "users" })]
    public virtual async Task<User> GetUserAsync(int userId)
    {
        var settings = _policyRegistry.GetSettingsFor<UserService>(nameof(GetUserAsync));
        var args = new object[] { userId };
        return await _cacheManager.GetOrCreateAsync<User>("GetUserAsync", args, 
            async () => { await Task.Delay(10); return User.Create(userId); }, settings, _keyGenerator);
    }

    [Cache(Duration = "00:01:00", Tags = new[] { "activity" })]
    public virtual async Task<List<string>> GetUserActivityAsync(int userId)
    {
        var settings = _policyRegistry.GetSettingsFor<UserService>(nameof(GetUserActivityAsync));
        var args = new object[] { userId };
        return await _cacheManager.GetOrCreateAsync<List<string>>("GetUserActivityAsync", args, 
            async () => { await Task.Delay(20); return new List<string> { $"Activity for user {userId}" }; }, 
            settings, _keyGenerator);
    }

    [Cache(Duration = "00:30:00", Tags = new[] { "preferences" })]
    public virtual async Task<object> GetUserPreferencesAsync(int userId)
    {
        var settings = _policyRegistry.GetSettingsFor<UserService>(nameof(GetUserPreferencesAsync));
        var args = new object[] { userId };
        return await _cacheManager.GetOrCreateAsync<object>("GetUserPreferencesAsync", args, 
            async () => { await Task.Delay(5); return new { Theme = "Dark", Language = "en" }; }, 
            settings, _keyGenerator);
    }

    [Cache(Duration = "24:00:00", Tags = new[] { "offline" })]
    public virtual async Task<User> GetUserForOfflineAsync(int userId)
    {
        var settings = _policyRegistry.GetSettingsFor<UserService>(nameof(GetUserForOfflineAsync));
        var args = new object[] { userId };
        return await _cacheManager.GetOrCreateAsync<User>("GetUserForOfflineAsync", args, 
            async () => { await Task.Delay(15); return User.Create(userId); }, settings, _keyGenerator);
    }

    [Cache(Duration = "00:10:00", Tags = new[] { "messages" })]
    public virtual async Task<List<string>> GetRecentMessagesAsync(int userId)
    {
        var settings = _policyRegistry.GetSettingsFor<UserService>(nameof(GetRecentMessagesAsync));
        var args = new object[] { userId };
        return await _cacheManager.GetOrCreateAsync<List<string>>("GetRecentMessagesAsync", args, 
            async () => { await Task.Delay(25); return new List<string> { $"Message for user {userId}" }; }, 
            settings, _keyGenerator);
    }

    [CacheInvalidate(Tags = new[] { "activity" })]
    public virtual async Task UpdateUserActivityAsync(int userId)
    {
        await Task.Delay(5);
        await _cacheManager.InvalidateByTagsAsync("activity");
    }

    [Cache(Duration = "01:00:00", Tags = new[] { "statistics" })]
    public virtual async Task<object> GetUserStatisticsAsync()
    {
        var settings = _policyRegistry.GetSettingsFor<UserService>(nameof(GetUserStatisticsAsync));
        var args = Array.Empty<object>();
        return await _cacheManager.GetOrCreateAsync<object>("GetUserStatisticsAsync", args, 
            async () => { await Task.Delay(500); return new { TotalUsers = 10000, ActiveUsers = 7500 }; }, 
            settings, _keyGenerator);
    }
}

// Product Service Interface and Implementation
public interface IProductService
{
    Task<Product> GetProductAsync(int productId);
    Task<object> GetProductInventoryAsync(int productId);
    Task<List<Product>> GetRelatedProductsAsync(int productId);
    Task UpdateProductInventoryAsync(int productId);
    Task<object> GetProductAnalyticsAsync();
}

public class ProductService : IProductService
{
    private readonly ICacheManager _cacheManager;
    private readonly IPolicyRegistry _policyRegistry;
    private readonly ICacheKeyGenerator _keyGenerator;

    public ProductService(ICacheManager cacheManager, IPolicyRegistry policyRegistry, ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _policyRegistry = policyRegistry;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:15:00", Tags = new[] { "products" })]
    public virtual async Task<Product> GetProductAsync(int productId)
    {
        var settings = _policyRegistry.GetSettingsFor<ProductService>(nameof(GetProductAsync));
        var args = new object[] { productId };
        return await _cacheManager.GetOrCreateAsync<Product>("GetProductAsync", args, 
            async () => { await Task.Delay(15); return Product.Create(productId); }, settings, _keyGenerator);
    }

    [Cache(Duration = "00:02:00", Tags = new[] { "inventory" })]
    public virtual async Task<object> GetProductInventoryAsync(int productId)
    {
        var settings = _policyRegistry.GetSettingsFor<ProductService>(nameof(GetProductInventoryAsync));
        var args = new object[] { productId };
        return await _cacheManager.GetOrCreateAsync<object>("GetProductInventoryAsync", args, 
            async () => { await Task.Delay(30); return new { ProductId = productId, Stock = Random.Shared.Next(0, 100) }; }, 
            settings, _keyGenerator);
    }

    [Cache(Duration = "01:00:00", Tags = new[] { "related" })]
    public virtual async Task<List<Product>> GetRelatedProductsAsync(int productId)
    {
        var settings = _policyRegistry.GetSettingsFor<ProductService>(nameof(GetRelatedProductsAsync));
        var args = new object[] { productId };
        return await _cacheManager.GetOrCreateAsync<List<Product>>("GetRelatedProductsAsync", args, 
            async () => { await Task.Delay(40); return Enumerable.Range(1, 5).Select(i => Product.Create(productId + i)).ToList(); }, 
            settings, _keyGenerator);
    }

    [CacheInvalidate(Tags = new[] { "inventory" })]
    public virtual async Task UpdateProductInventoryAsync(int productId)
    {
        await Task.Delay(10);
        await _cacheManager.InvalidateByTagsAsync("inventory");
    }

    [Cache(Duration = "00:30:00", Tags = new[] { "analytics" })]
    public virtual async Task<object> GetProductAnalyticsAsync()
    {
        var settings = _policyRegistry.GetSettingsFor<ProductService>(nameof(GetProductAnalyticsAsync));
        var args = Array.Empty<object>();
        return await _cacheManager.GetOrCreateAsync<object>("GetProductAnalyticsAsync", args, 
            async () => { await Task.Delay(300); return new { TotalProducts = 5000, BestSellers = 50 }; }, 
            settings, _keyGenerator);
    }
}

// API Service Interface and Implementation
public interface IApiService
{
    Task<object> GetWeatherDataAsync(int cityId);
    Task<object> GetStockDataAsync(string symbol);
    Task<object> GetNewsDataAsync();
    Task<object> GetAppConfigurationAsync();
    Task<object> GetSystemMetricsAsync();
}

public class ApiService : IApiService
{
    private readonly ICacheManager _cacheManager;
    private readonly IPolicyRegistry _policyRegistry;
    private readonly ICacheKeyGenerator _keyGenerator;

    public ApiService(ICacheManager cacheManager, IPolicyRegistry policyRegistry, ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _policyRegistry = policyRegistry;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:10:00", Tags = new[] { "weather" })]
    public virtual async Task<object> GetWeatherDataAsync(int cityId)
    {
        var settings = _policyRegistry.GetSettingsFor<ApiService>(nameof(GetWeatherDataAsync));
        var args = new object[] { cityId };
        return await _cacheManager.GetOrCreateAsync<object>("GetWeatherDataAsync", args, 
            async () => { await Task.Delay(100); return new { CityId = cityId, Temperature = Random.Shared.Next(-10, 40) }; }, 
            settings, _keyGenerator);
    }

    [Cache(Duration = "00:01:00", Tags = new[] { "stocks" })]
    public virtual async Task<object> GetStockDataAsync(string symbol)
    {
        var settings = _policyRegistry.GetSettingsFor<ApiService>(nameof(GetStockDataAsync));
        var args = new object[] { symbol };
        return await _cacheManager.GetOrCreateAsync<object>("GetStockDataAsync", args, 
            async () => { await Task.Delay(200); return new { Symbol = symbol, Price = Random.Shared.NextDouble() * 1000 }; }, 
            settings, _keyGenerator);
    }

    [Cache(Duration = "00:05:00", Tags = new[] { "news" })]
    public virtual async Task<object> GetNewsDataAsync()
    {
        var settings = _policyRegistry.GetSettingsFor<ApiService>(nameof(GetNewsDataAsync));
        var args = Array.Empty<object>();
        return await _cacheManager.GetOrCreateAsync<object>("GetNewsDataAsync", args, 
            async () => { await Task.Delay(150); return new { Headlines = new[] { "News 1", "News 2", "News 3" } }; }, 
            settings, _keyGenerator);
    }

    [Cache(Duration = "12:00:00", Tags = new[] { "config" })]
    public virtual async Task<object> GetAppConfigurationAsync()
    {
        var settings = _policyRegistry.GetSettingsFor<ApiService>(nameof(GetAppConfigurationAsync));
        var args = Array.Empty<object>();
        return await _cacheManager.GetOrCreateAsync<object>("GetAppConfigurationAsync", args, 
            async () => { await Task.Delay(50); return new { Version = "1.0", Features = new[] { "feature1", "feature2" } }; }, 
            settings, _keyGenerator);
    }

    [Cache(Duration = "00:05:00", Tags = new[] { "metrics" })]
    public virtual async Task<object> GetSystemMetricsAsync()
    {
        var settings = _policyRegistry.GetSettingsFor<ApiService>(nameof(GetSystemMetricsAsync));
        var args = Array.Empty<object>();
        return await _cacheManager.GetOrCreateAsync<object>("GetSystemMetricsAsync", args, 
            async () => { await Task.Delay(400); return new { CPU = Random.Shared.NextDouble() * 100, Memory = Random.Shared.NextDouble() * 100 }; }, 
            settings, _keyGenerator);
    }
}
