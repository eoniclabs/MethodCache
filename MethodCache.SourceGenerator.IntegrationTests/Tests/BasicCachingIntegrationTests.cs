using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Integration tests for basic caching scenarios with real source-generated code
/// </summary>
public class BasicCachingIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public BasicCachingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_BasicCaching_Works()
    {
        // Arrange: Define a test service with cache attributes
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; }
        
        public override bool Equals(object? obj)
        {
            return obj is User user && Id == user.Id && Name == user.Name;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(Id, Name);
        }
    }

    public interface IUserService
    {
        [Cache(Duration = ""00:01:00"", Tags = new[] { ""users"" })]
        Task<User> GetUserAsync(int id);
        
        [Cache(Duration = ""00:02:00"", Tags = new[] { ""users"", ""usercount"" })]
        Task<int> GetUserCountAsync();
        
        [CacheInvalidate(Tags = new[] { ""users"", ""usercount"" })]
        Task UpdateUserAsync(User user);
    }

    public class UserService : IUserService
    {
        private static int _getUserCallCount = 0;
        private static int _getUserCountCallCount = 0;
        
        public virtual async Task<User> GetUserAsync(int id)
        {
            _getUserCallCount++;
            await Task.Delay(10); // Simulate database call
            return new User 
            { 
                Id = id, 
                Name = $""User {id}"", 
                Email = $""user{id}@test.com"",
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };
        }
        
        public virtual async Task<int> GetUserCountAsync()
        {
            _getUserCountCallCount++;
            await Task.Delay(50); // Simulate expensive database query
            return 42; // Simulated user count
        }
        
        public virtual async Task UpdateUserAsync(User user)
        {
            await Task.Delay(20); // Simulate database update
        }
        
        public static void ResetCallCounts()
        {
            _getUserCallCount = 0;
            _getUserCountCallCount = 0;
        }
        
        public static int GetUserCallCount => _getUserCallCount;
        public static int GetUserCountCallCount => _getUserCountCallCount;
    }
}";

        // Act: Compile with source generator
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        
        // Debug: Output generated sources
        _output.WriteLine("=== GENERATED SOURCES ===");
        testAssembly.DumpGeneratedSources(new StringWriter());

        // Setup DI container with test metrics
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        // Get the generated cached service
        var userServiceType = testAssembly.Assembly.GetType("TestNamespace.IUserService");
        Assert.NotNull(userServiceType);
        
        var userService = serviceProvider.GetService(userServiceType);
        Assert.NotNull(userService);

        // Reset counters
        var userServiceImplType = testAssembly.Assembly.GetType("TestNamespace.UserService");
        var resetMethod = userServiceImplType?.GetMethod("ResetCallCounts");
        resetMethod?.Invoke(null, null);
        metricsProvider.Reset();

        // Test basic caching behavior
        var getUserMethod = userServiceType.GetMethod("GetUserAsync");
        Assert.NotNull(getUserMethod);

        // First call - should be cache miss
        var user1Task = (Task)getUserMethod.Invoke(userService, new object[] { 1 })!;
        var user1 = await GetTaskResult<User>(user1Task);
        
        await metricsProvider.WaitForMetricsAsync(expectedMisses: 1);
        var metrics1 = metricsProvider.Metrics;
        Assert.Equal(0, metrics1.HitCount);
        Assert.Equal(1, metrics1.MissCount);

        // Second call with same parameters - should be cache hit
        var user2Task = (Task)getUserMethod.Invoke(userService, new object[] { 1 })!;
        var user2 = await GetTaskResult<User>(user2Task);
        
        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 1);
        var metrics2 = metricsProvider.Metrics;
        Assert.Equal(1, metrics2.HitCount);
        Assert.Equal(1, metrics2.MissCount);

        // Verify cached data is the same
        Assert.Equal(user1.Id, user2.Id);
        Assert.Equal(user1.Name, user2.Name);

        // Verify the underlying method was only called once
        var getCallCountMethod = userServiceImplType?.GetMethod("get_GetUserCallCount");
        var callCount = (int)getCallCountMethod?.Invoke(null, null)!;
        Assert.Equal(1, callCount);

        // Test the user count method for additional coverage
        var getUserCountMethod = userServiceType.GetMethod("GetUserCountAsync");
        Assert.NotNull(getUserCountMethod);

        // First call - should be cache miss
        var count1Task = (Task)getUserCountMethod.Invoke(userService, new object[] { })!;
        var count1 = await GetTaskResult<int>(count1Task);
        Assert.Equal(42, count1);

        // Second call - should be cache hit 
        var count2Task = (Task)getUserCountMethod.Invoke(userService, new object[] { })!;
        var count2 = await GetTaskResult<int>(count2Task);
        Assert.Equal(42, count2);

        // Verify count method was only called once
        var getUserCountCallCountMethod = userServiceImplType?.GetMethod("get_GetUserCountCallCount");
        var countCallCount = (int)getUserCountCallCountMethod?.Invoke(null, null)!;
        Assert.Equal(1, countCallCount);

        _output.WriteLine($"✅ Basic caching test passed! Hits: {metrics2.HitCount}, Misses: {metrics2.MissCount}");
    }

    [Fact]
    public async Task SourceGenerator_TagBasedInvalidation_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string Category { get; set; } = string.Empty;
        public bool InStock { get; set; }
    }

    public interface IProductService
    {
        [Cache(Duration = ""00:05:00"", Tags = new[] { ""products"", ""product-details"" })]
        Task<Product> GetProductAsync(int id);
        
        [Cache(Duration = ""00:03:00"", Tags = new[] { ""products"", ""product-count"" })]
        Task<int> GetProductCountByCategoryAsync(string category);
        
        [CacheInvalidate(Tags = new[] { ""products"" })]
        Task UpdateProductAsync(Product product);
        
        [CacheInvalidate(Tags = new[] { ""product-count"" })]
        Task RefreshProductCountAsync();
    }

    public class ProductService : IProductService
    {
        private static int _getProductCallCount = 0;
        private static int _getCategoryCountCallCount = 0;
        
        public virtual async Task<Product> GetProductAsync(int id)
        {
            _getProductCallCount++;
            await Task.Delay(5);
            return new Product 
            { 
                Id = id, 
                Name = $""Product {id}"", 
                Price = id * 10.99m,
                Category = ""Electronics"",
                InStock = true
            };
        }
        
        public virtual async Task<int> GetProductCountByCategoryAsync(string category)
        {
            _getCategoryCountCallCount++;
            await Task.Delay(20);
            return 15; // Simulated product count for category
        }
        
        public virtual async Task UpdateProductAsync(Product product)
        {
            await Task.Delay(10);
        }
        
        public virtual async Task RefreshProductCountAsync()
        {
            await Task.Delay(5);
        }
        
        public static void ResetCallCounts()
        {
            _getProductCallCount = 0;
            _getCategoryCountCallCount = 0;
        }
        
        public static int GetProductCallCount => _getProductCallCount;
        public static int GetCategoryCountCallCount => _getCategoryCountCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var productServiceType = testAssembly.Assembly.GetType("TestNamespace.IProductService");
        var productService = serviceProvider.GetService(productServiceType);
        
        // Reset state
        var implType = testAssembly.Assembly.GetType("TestNamespace.ProductService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        // Test caching
        var getProductMethod = productServiceType!.GetMethod("GetProductAsync");
        var getCategoryCountMethod = productServiceType.GetMethod("GetProductCountByCategoryAsync");
        var updateProductMethod = productServiceType.GetMethod("UpdateProductAsync");

        // Cache some data
        var product1Task = (Task)getProductMethod!.Invoke(productService, new object[] { 1 })!;
        var product1 = await GetTaskResult<Product>(product1Task);
        
        var categoryCountTask = (Task)getCategoryCountMethod!.Invoke(productService, new object[] { "Electronics" })!;
        var categoryCount = await GetTaskResult<int>(categoryCountTask);
        Assert.Equal(15, categoryCount);

        await metricsProvider.WaitForMetricsAsync(expectedMisses: 2);
        Assert.Equal(2, metricsProvider.Metrics.MissCount);

        // Verify data is cached (second calls should be hits)
        var product1AgainTask = (Task)getProductMethod.Invoke(productService, new object[] { 1 })!;
        await GetTaskResult<Product>(product1AgainTask);
        
        var categoryCountAgainTask = (Task)getCategoryCountMethod.Invoke(productService, new object[] { "Electronics" })!;
        var categoryCountAgain = await GetTaskResult<int>(categoryCountAgainTask);
        Assert.Equal(15, categoryCountAgain);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        Assert.Equal(2, metricsProvider.Metrics.HitCount);

        // Now invalidate with "products" tag - should clear both caches
        var updateTask = (Task)updateProductMethod!.Invoke(productService, new object[] { product1 })!;
        await updateTask;

        // Verify invalidation happened
        Assert.True(metricsProvider.Metrics.TagInvalidations.ContainsKey("products"));

        // Calls after invalidation should be cache misses
        var product1AfterUpdateTask = (Task)getProductMethod.Invoke(productService, new object[] { 1 })!;
        await GetTaskResult<Product>(product1AfterUpdateTask);
        
        var categoryCountAfterUpdateTask = (Task)getCategoryCountMethod.Invoke(productService, new object[] { "Electronics" })!;
        await GetTaskResult<int>(categoryCountAfterUpdateTask);

        await metricsProvider.WaitForMetricsAsync(expectedMisses: 4);
        var finalMetrics = metricsProvider.Metrics;
        Assert.Equal(4, finalMetrics.MissCount); // 2 initial + 2 after invalidation

        _output.WriteLine($"✅ Tag-based invalidation test passed! Final metrics - Hits: {finalMetrics.HitCount}, Misses: {finalMetrics.MissCount}");
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
    }

}