using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using MethodCache.SourceGenerator.IntegrationTests.Models;
using System.Reflection;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Integration tests for error handling and edge cases in cache scenarios
/// </summary>
public class ErrorHandlingIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public ErrorHandlingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_ExceptionHandling_DoesNotCacheExceptions()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public interface IErrorService
    {
        [Cache(Duration = ""00:02:00"")]
        Task<string> GetDataAsync(int id);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> GetDataWithRetryAsync(int id, bool shouldFail);
    }

    public class ErrorService : IErrorService
    {
        private static int _getDataCallCount = 0;
        private static int _retryCallCount = 0;
        
        public virtual async Task<string> GetDataAsync(int id)
        {
            _getDataCallCount++;
            await Task.Delay(5);
            
            if (id < 0)
                throw new ArgumentException(""ID cannot be negative"");
                
            if (id == 999)
                throw new InvalidOperationException(""Service unavailable"");
                
            return $""Data-{id}"";
        }
        
        public virtual async Task<string> GetDataWithRetryAsync(int id, bool shouldFail)
        {
            _retryCallCount++;
            await Task.Delay(5);
            
            if (shouldFail)
                throw new TimeoutException(""Request timed out"");
                
            return $""RetryData-{id}"";
        }
        
        public static void ResetCallCounts()
        {
            _getDataCallCount = 0;
            _retryCallCount = 0;
        }
        
        public static int GetDataCallCount => _getDataCallCount;
        public static int RetryCallCount => _retryCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IErrorService");
        var service = serviceProvider.GetService(serviceType);
        var implType = testAssembly.Assembly.GetType("TestNamespace.ErrorService");
        
        Assert.NotNull(service);
        
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var getDataMethod = serviceType!.GetMethod("GetDataAsync");
        var retryMethod = serviceType.GetMethod("GetDataWithRetryAsync");

        // Test successful call - should be cached
        var successTask1 = (Task)getDataMethod!.Invoke(service, new object[] { 1 })!;
        var result1 = await GetTaskResult<string>(successTask1);
        
        var successTask2 = (Task)getDataMethod.Invoke(service, new object[] { 1 })!;
        var result2 = await GetTaskResult<string>(successTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 1);
        Assert.Equal(1, metricsProvider.Metrics.HitCount);
        Assert.Equal(1, metricsProvider.Metrics.MissCount);
        Assert.Equal(result1, result2);

        // Test exception - should not be cached, each call should throw
        await Assert.ThrowsAsync<TargetInvocationException>(async () =>
        {
            var errorTask = (Task)getDataMethod.Invoke(service, new object[] { -1 })!;
            await errorTask;
        });

        await Assert.ThrowsAsync<TargetInvocationException>(async () =>
        {
            var errorTask = (Task)getDataMethod.Invoke(service, new object[] { -1 })!;
            await errorTask;
        });

        // Should have recorded 2 errors, no additional hits/misses for the error case
        await Task.Delay(100); // Allow metrics to be recorded
        var errorMetrics = metricsProvider.Metrics;
        Assert.Equal(2, errorMetrics.ErrorCount);

        // Test retry scenario - exception then success
        await Assert.ThrowsAsync<TargetInvocationException>(async () =>
        {
            var failTask = (Task)retryMethod!.Invoke(service, new object[] { 2, true })!;
            await failTask;
        });

        // Now succeed - should cache this result
        var retrySuccessTask1 = (Task)retryMethod!.Invoke(service, new object[] { 2, false })!;
        var retryResult1 = await GetTaskResult<string>(retrySuccessTask1);
        
        var retrySuccessTask2 = (Task)retryMethod.Invoke(service, new object[] { 2, false })!;
        var retryResult2 = await GetTaskResult<string>(retrySuccessTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 3);
        var finalMetrics = metricsProvider.Metrics;
        
        Assert.Equal(2, finalMetrics.HitCount); // 1 original + 1 retry success
        Assert.Equal(3, finalMetrics.MissCount); // 1 original + 1 retry fail + 1 retry success
        Assert.Equal(3, finalMetrics.ErrorCount); // 2 negative ID + 1 retry fail
        Assert.Equal(retryResult1, retryResult2);

        _output.WriteLine($"✅ Exception handling test passed! Exceptions not cached, errors recorded: {finalMetrics.ErrorCount}");
    }

    [Fact]
    public async Task SourceGenerator_NullValues_HandledCorrectly()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public interface INullableService
    {
        [Cache(Duration = ""00:02:00"")]
        Task<string?> GetNullableStringAsync(int id);
        
        [Cache(Duration = ""00:02:00"")]
        Task<object?> GetNullableObjectAsync(string key);
    }

    public class NullableService : INullableService
    {
        private static int _stringCallCount = 0;
        private static int _objectCallCount = 0;
        
        public virtual async Task<string?> GetNullableStringAsync(int id)
        {
            _stringCallCount++;
            await Task.Delay(5);
            
            return id == 0 ? null : $""String-{id}"";
        }
        
        public virtual async Task<object?> GetNullableObjectAsync(string key)
        {
            _objectCallCount++;
            await Task.Delay(5);
            
            return key == ""null"" ? null : new { Key = key, Value = $""Object-{key}"" };
        }
        
        public static void ResetCallCounts()
        {
            _stringCallCount = 0;
            _objectCallCount = 0;
        }
        
        public static int StringCallCount => _stringCallCount;
        public static int ObjectCallCount => _objectCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.INullableService");
        var service = serviceProvider.GetService(serviceType);
        var implType = testAssembly.Assembly.GetType("TestNamespace.NullableService");
        
        Assert.NotNull(service);
        
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var stringMethod = serviceType!.GetMethod("GetNullableStringAsync");
        var objectMethod = serviceType.GetMethod("GetNullableObjectAsync");

        // Test null string result - should be cached
        var nullStringTask1 = (Task)stringMethod!.Invoke(service, new object[] { 0 })!;
        var nullString1 = await GetTaskResult<string>(nullStringTask1);
        
        var nullStringTask2 = (Task)stringMethod.Invoke(service, new object[] { 0 })!;
        var nullString2 = await GetTaskResult<string>(nullStringTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 1);
        Assert.Null(nullString1);
        Assert.Null(nullString2);

        // Test non-null string result - should be cached
        var stringTask1 = (Task)stringMethod.Invoke(service, new object[] { 1 })!;
        var string1 = await GetTaskResult<string>(stringTask1);
        
        var stringTask2 = (Task)stringMethod.Invoke(service, new object[] { 1 })!;
        var string2 = await GetTaskResult<string>(stringTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        Assert.Equal("String-1", string1);
        Assert.Equal(string1, string2);

        // Test null object result - should be cached
        var nullObjectTask1 = (Task)objectMethod!.Invoke(service, new object[] { "null" })!;
        var nullObject1 = await GetTaskResult<object>(nullObjectTask1);
        
        var nullObjectTask2 = (Task)objectMethod.Invoke(service, new object[] { "null" })!;
        var nullObject2 = await GetTaskResult<object>(nullObjectTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 3, expectedMisses: 3);
        Assert.Null(nullObject1);
        Assert.Null(nullObject2);

        // Test non-null object result - should be cached
        var objectTask1 = (Task)objectMethod.Invoke(service, new object[] { "test" })!;
        var object1 = await GetTaskResult<object>(objectTask1);
        
        var objectTask2 = (Task)objectMethod.Invoke(service, new object[] { "test" })!;
        var object2 = await GetTaskResult<object>(objectTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 4, expectedMisses: 4);
        var finalMetrics = metricsProvider.Metrics;
        
        Assert.Equal(4, finalMetrics.HitCount);
        Assert.Equal(4, finalMetrics.MissCount);
        Assert.NotNull(object1);
        Assert.NotNull(object2);

        // Verify call counts - each method called twice (once for null, once for non-null)
        var stringCount = (int)implType?.GetProperty("StringCallCount")?.GetValue(null)!;
        var objectCount = (int)implType?.GetProperty("ObjectCallCount")?.GetValue(null)!;
        
        Assert.Equal(2, stringCount);
        Assert.Equal(2, objectCount);

        _output.WriteLine($"✅ Null value handling test passed! Null values cached correctly");
    }

    [Fact]
    public async Task SourceGenerator_LargeObjects_HandledCorrectly()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace TestNamespace
{
    public interface ILargeObjectService
    {
        [Cache(Duration = ""00:02:00"")]
        Task<Order> GetLargeOrderAsync(int orderId);
        
        [Cache(Duration = ""00:02:00"")]
        Task<List<Product>> GetManyProductsAsync(int count);
    }

    public class LargeObjectService : ILargeObjectService
    {
        private static int _orderCallCount = 0;
        private static int _productsCallCount = 0;
        
        public virtual async Task<Order> GetLargeOrderAsync(int orderId)
        {
            _orderCallCount++;
            await Task.Delay(20);
            
            var order = new Order
            {
                Id = orderId,
                UserId = 1,
                Total = 0,
                CreatedAt = DateTime.UtcNow,
                Status = OrderStatus.Processing,
                Items = new List<OrderItem>()
            };
            
            // Create a large order with many items
            for (int i = 1; i <= 100; i++)
            {
                var item = new OrderItem
                {
                    ProductId = i,
                    Quantity = i % 5 + 1,
                    Price = i * 9.99m
                };
                order.Items.Add(item);
                order.Total += item.Price * item.Quantity;
            }
            
            return order;
        }
        
        public virtual async Task<List<Product>> GetManyProductsAsync(int count)
        {
            _productsCallCount++;
            await Task.Delay(30);
            
            var products = new List<Product>();
            for (int i = 1; i <= count; i++)
            {
                products.Add(new Product
                {
                    Id = i,
                    Name = $""Product {i} with very long description that contains many details about the product features and specifications"",
                    Price = i * 12.99m,
                    Category = $""Category {i % 10}"",
                    InStock = i % 3 != 0
                });
            }
            
            return products;
        }
        
        public static void ResetCallCounts()
        {
            _orderCallCount = 0;
            _productsCallCount = 0;
        }
        
        public static int OrderCallCount => _orderCallCount;
        public static int ProductsCallCount => _productsCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.ILargeObjectService");
        var service = serviceProvider.GetService(serviceType);
        var implType = testAssembly.Assembly.GetType("TestNamespace.LargeObjectService");
        
        Assert.NotNull(service);
        
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var orderMethod = serviceType!.GetMethod("GetLargeOrderAsync");
        var productsMethod = serviceType.GetMethod("GetManyProductsAsync");

        // Test large order caching
        var orderTask1 = (Task)orderMethod!.Invoke(service, new object[] { 1 })!;
        var order1 = await GetTaskResult<Order>(orderTask1);
        
        var orderTask2 = (Task)orderMethod.Invoke(service, new object[] { 1 })!;
        var order2 = await GetTaskResult<Order>(orderTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 1);
        Assert.Equal(order1.Id, order2.Id);
        Assert.Equal(order1.Items.Count, order2.Items.Count);
        Assert.Equal(100, order1.Items.Count); // Verify large object size

        // Test large product list caching
        var productsTask1 = (Task)productsMethod!.Invoke(service, new object[] { 500 })!;
        var products1 = await GetTaskResult<List<Product>>(productsTask1);
        
        var productsTask2 = (Task)productsMethod.Invoke(service, new object[] { 500 })!;
        var products2 = await GetTaskResult<List<Product>>(productsTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        var finalMetrics = metricsProvider.Metrics;
        
        Assert.Equal(2, finalMetrics.HitCount);
        Assert.Equal(2, finalMetrics.MissCount);
        Assert.Equal(products1.Count, products2.Count);
        Assert.Equal(500, products1.Count); // Verify large collection size

        // Verify call counts - each method called once (second calls were cached)
        var orderCount = (int)implType?.GetProperty("OrderCallCount")?.GetValue(null)!;
        var productsCount = (int)implType?.GetProperty("ProductsCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, orderCount);
        Assert.Equal(1, productsCount);

        _output.WriteLine($"✅ Large object handling test passed! Cached order with {order1.Items.Count} items and {products1.Count} products");
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
    }
}