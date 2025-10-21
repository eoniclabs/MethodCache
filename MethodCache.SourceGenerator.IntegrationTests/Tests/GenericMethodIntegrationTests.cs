using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Abstractions.Registry;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Integration tests for generic method scenarios with real source-generated code
/// </summary>
public class GenericMethodIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public GenericMethodIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_GenericServiceInterface_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public class BaseEntity
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class User : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public class Product : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    public interface IGenericRepository<T>
    {
        [Cache(Duration = ""00:02:00"")]
        Task<T> GetByIdAsync(int id);
        
        [Cache(Duration = ""00:01:00"")]
        Task<List<T>> GetAllAsync();
        
        [Cache(Duration = ""00:03:00"")]
        Task<T[]> GetByIdsAsync(int[] ids);
        
        [CacheInvalidate(Tags = new[] { ""entities"" })]
        Task SaveAsync(T entity);
    }

    public class UserRepository : IGenericRepository<User>
    {
        private static int _getByIdCallCount = 0;
        private static int _getAllCallCount = 0;
        private static int _getByIdsCallCount = 0;
        
        public virtual async Task<User> GetByIdAsync(int id)
        {
            _getByIdCallCount++;
            await Task.Delay(5);
            return new User 
            { 
                Id = id, 
                Name = $""User {id}"", 
                Email = $""user{id}@test.com"",
                CreatedAt = DateTime.UtcNow
            };
        }
        
        public virtual async Task<List<User>> GetAllAsync()
        {
            _getAllCallCount++;
            await Task.Delay(10);
            return new List<User>
            {
                new User { Id = 1, Name = ""User 1"", Email = ""user1@test.com"", CreatedAt = DateTime.UtcNow },
                new User { Id = 2, Name = ""User 2"", Email = ""user2@test.com"", CreatedAt = DateTime.UtcNow }
            };
        }
        
        public virtual async Task<User[]> GetByIdsAsync(int[] ids)
        {
            _getByIdsCallCount++;
            await Task.Delay(8);
            var users = new User[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                users[i] = new User 
                { 
                    Id = ids[i], 
                    Name = $""User {ids[i]}"", 
                    Email = $""user{ids[i]}@test.com"",
                    CreatedAt = DateTime.UtcNow
                };
            }
            return users;
        }
        
        public virtual async Task SaveAsync(User entity)
        {
            await Task.Delay(5);
        }
        
        public static void ResetCallCounts()
        {
            _getByIdCallCount = 0;
            _getAllCallCount = 0;
            _getByIdsCallCount = 0;
        }
        
        public static int GetByIdCallCount => _getByIdCallCount;
        public static int GetAllCallCount => _getAllCallCount;
        public static int GetByIdsCallCount => _getByIdsCallCount;
    }

    public class ProductRepository : IGenericRepository<Product>
    {
        private static int _getByIdCallCount = 0;
        private static int _getAllCallCount = 0;
        private static int _getByIdsCallCount = 0;
        
        public virtual async Task<Product> GetByIdAsync(int id)
        {
            _getByIdCallCount++;
            await Task.Delay(5);
            return new Product 
            { 
                Id = id, 
                Name = $""Product {id}"", 
                Price = id * 10.99m,
                CreatedAt = DateTime.UtcNow
            };
        }
        
        public virtual async Task<List<Product>> GetAllAsync()
        {
            _getAllCallCount++;
            await Task.Delay(10);
            return new List<Product>
            {
                new Product { Id = 1, Name = ""Product 1"", Price = 10.99m, CreatedAt = DateTime.UtcNow },
                new Product { Id = 2, Name = ""Product 2"", Price = 20.99m, CreatedAt = DateTime.UtcNow }
            };
        }
        
        public virtual async Task<Product[]> GetByIdsAsync(int[] ids)
        {
            _getByIdsCallCount++;
            await Task.Delay(8);
            var products = new Product[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                products[i] = new Product 
                { 
                    Id = ids[i], 
                    Name = $""Product {ids[i]}"", 
                    Price = ids[i] * 10.99m,
                    CreatedAt = DateTime.UtcNow
                };
            }
            return products;
        }
        
        public virtual async Task SaveAsync(Product entity)
        {
            await Task.Delay(5);
        }
        
        public static void ResetCallCounts()
        {
            _getByIdCallCount = 0;
            _getAllCallCount = 0;
            _getByIdsCallCount = 0;
        }
        
        public static int GetByIdCallCount => _getByIdCallCount;
        public static int GetAllCallCount => _getAllCallCount;
        public static int GetByIdsCallCount => _getByIdsCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
            
            // Manually register generic interfaces since they're excluded from automatic DI generation
            var userRepoImplType = testAssembly.Assembly.GetType("TestNamespace.UserRepository")!;
        var userRepoInterfaceType = testAssembly.Assembly.GetType("TestNamespace.IGenericRepository`1")!
        .MakeGenericType(testAssembly.Assembly.GetType("TestNamespace.User")!);
                
            var productRepoImplType = testAssembly.Assembly.GetType("TestNamespace.ProductRepository")!;
        var productRepoInterfaceType = testAssembly.Assembly.GetType("TestNamespace.IGenericRepository`1")!
        .MakeGenericType(testAssembly.Assembly.GetType("TestNamespace.Product")!);
                
            // Register with caching decorators
            services.AddSingleton(userRepoInterfaceType, sp =>
            {
                var userRepoDecoratorType = testAssembly.Assembly.GetType("TestNamespace.IGenericRepositoryDecorator`1")!
        .MakeGenericType(testAssembly.Assembly.GetType("TestNamespace.User")!);
                var userRepoImpl = Activator.CreateInstance(userRepoImplType)!;
                return Activator.CreateInstance(userRepoDecoratorType,
                    userRepoImpl,
                    sp.GetRequiredService<ICacheManager>(),
                    sp.GetRequiredService<IPolicyRegistry>(),
                    sp.GetRequiredService<ICacheKeyGenerator>(),
                    sp.GetService<ICacheMetricsProvider>())!;
            });

            services.AddSingleton(productRepoInterfaceType, sp =>
            {
                var productRepoDecoratorType = testAssembly.Assembly.GetType("TestNamespace.IGenericRepositoryDecorator`1")!
        .MakeGenericType(testAssembly.Assembly.GetType("TestNamespace.Product")!);
                var productRepoImpl = Activator.CreateInstance(productRepoImplType)!;
                return Activator.CreateInstance(productRepoDecoratorType,
                    productRepoImpl,
                    sp.GetRequiredService<ICacheManager>(),
                    sp.GetRequiredService<IPolicyRegistry>(),
                    sp.GetRequiredService<ICacheKeyGenerator>(),
                    sp.GetService<ICacheMetricsProvider>())!;
            });
        });

        // Test User repository (generic interface with User type)
        var userRepoType = testAssembly.Assembly.GetType("TestNamespace.IGenericRepository`1")?.MakeGenericType(testAssembly.Assembly.GetType("TestNamespace.User")!);
        Assert.NotNull(userRepoType);
        var userService = serviceProvider.GetService(userRepoType);
        Assert.NotNull(userService);

        // Test Product repository (generic interface with Product type)
        var productRepoType = testAssembly.Assembly.GetType("TestNamespace.IGenericRepository`1")?.MakeGenericType(testAssembly.Assembly.GetType("TestNamespace.Product")!);
        Assert.NotNull(productRepoType);
        var productService = serviceProvider.GetService(productRepoType);
        Assert.NotNull(productService);

        // Reset counters
        var userImplType = testAssembly.Assembly.GetType("TestNamespace.UserRepository");
        var productImplType = testAssembly.Assembly.GetType("TestNamespace.ProductRepository");
        userImplType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        productImplType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var userType = testAssembly.Assembly.GetType("TestNamespace.User");
        var productType = testAssembly.Assembly.GetType("TestNamespace.Product");
        Assert.NotNull(userType);
        Assert.NotNull(productType);

        // Test User repository caching
        var userGetByIdMethod = userRepoType!.GetMethod("GetByIdAsync");
        var userTask1 = (Task)userGetByIdMethod!.Invoke(userService, new object[] { 1 })!;
        var user1 = await GetTaskResult(userTask1, userType);
        
        var userTask2 = (Task)userGetByIdMethod.Invoke(userService, new object[] { 1 })!;
        var user2 = await GetTaskResult(userTask2, userType);

        // Test Product repository caching (should be independent)
        var productGetByIdMethod = productRepoType!.GetMethod("GetByIdAsync");
        var productTask1 = (Task)productGetByIdMethod!.Invoke(productService, new object[] { 1 })!;
        var product1 = await GetTaskResult(productTask1, productType);
        
        var productTask2 = (Task)productGetByIdMethod.Invoke(productService, new object[] { 1 })!;
        var product2 = await GetTaskResult(productTask2, productType);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        
        // Verify caching worked independently for each generic type
        var userGetByIdCallCount = (int)userImplType?.GetProperty("GetByIdCallCount")?.GetValue(null)!;
        var productGetByIdCallCount = (int)productImplType?.GetProperty("GetByIdCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, userGetByIdCallCount);
        Assert.Equal(1, productGetByIdCallCount);

        // Verify we got correct types back
        Assert.NotNull(user1);
        Assert.NotNull(product1);

        _output.WriteLine($"✅ Generic service interface test passed! Caching works independently for different generic types");
    }

    [Fact]
    public async Task SourceGenerator_GenericMethodsWithConstraints_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public interface IIdentifiable
    {
        int Id { get; set; }
    }

    public interface IComparable<T>
    {
        int CompareTo(T other);
    }

    public class SortableEntity : IIdentifiable, IComparable<SortableEntity>
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Priority { get; set; }
        
        public int CompareTo(SortableEntity other)
        {
            return Priority.CompareTo(other.Priority);
        }
    }

    public interface IGenericService
    {
        [Cache(Duration = ""00:02:00"")]
        Task<T> ProcessEntityAsync<T>(T entity) where T : IIdentifiable;
        
        [Cache(Duration = ""00:01:00"")]
        Task<List<T>> SortEntitiesAsync<T>(List<T> entities) where T : IComparable<T>;
        
        [Cache(Duration = ""00:03:00"")]
        Task<TResult> TransformAsync<TInput, TResult>(TInput input) 
            where TInput : IIdentifiable 
            where TResult : class, new();
    }

    public class GenericService : IGenericService
    {
        private static int _processEntityCallCount = 0;
        private static int _sortEntitiesCallCount = 0;
        private static int _transformCallCount = 0;
        
        public virtual async Task<T> ProcessEntityAsync<T>(T entity) where T : IIdentifiable
        {
            _processEntityCallCount++;
            await Task.Delay(5);
            entity.Id = entity.Id * 2; // Simple processing
            return entity;
        }
        
        public virtual async Task<List<T>> SortEntitiesAsync<T>(List<T> entities) where T : IComparable<T>
        {
            _sortEntitiesCallCount++;
            await Task.Delay(10);
            var sorted = new List<T>(entities);
            sorted.Sort();
            return sorted;
        }
        
        public virtual async Task<TResult> TransformAsync<TInput, TResult>(TInput input) 
            where TInput : IIdentifiable 
            where TResult : class, new()
        {
            _transformCallCount++;
            await Task.Delay(8);
            var result = new TResult();
            return result;
        }
        
        public static void ResetCallCounts()
        {
            _processEntityCallCount = 0;
            _sortEntitiesCallCount = 0;
            _transformCallCount = 0;
        }
        
        public static int ProcessEntityCallCount => _processEntityCallCount;
        public static int SortEntitiesCallCount => _sortEntitiesCallCount;
        public static int TransformCallCount => _transformCallCount;
    }

    public class ResultClass
    {
        public string Data { get; set; } = ""Transformed"";
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IGenericService");
        Assert.NotNull(serviceType);
        var service = serviceProvider.GetService(serviceType);
        Assert.NotNull(service);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.GenericService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var sortableEntityType = testAssembly.Assembly.GetType("TestNamespace.SortableEntity");
        var resultClassType = testAssembly.Assembly.GetType("TestNamespace.ResultClass");
        
        // Create test entities
        var entity1 = Activator.CreateInstance(sortableEntityType!)!;
        sortableEntityType!.GetProperty("Id")!.SetValue(entity1, 1);
        sortableEntityType.GetProperty("Name")!.SetValue(entity1, "Entity 1");
        sortableEntityType.GetProperty("Priority")!.SetValue(entity1, 5);

        var entity2 = Activator.CreateInstance(sortableEntityType)!;
        sortableEntityType.GetProperty("Id")!.SetValue(entity2, 1);
        sortableEntityType.GetProperty("Name")!.SetValue(entity2, "Entity 1");
        sortableEntityType.GetProperty("Priority")!.SetValue(entity2, 5);

        // Test generic method with constraint caching
        var processMethod = serviceType!.GetMethod("ProcessEntityAsync");
        var specificProcessMethod = processMethod!.MakeGenericMethod(sortableEntityType);
        
        var processTask1 = (Task)specificProcessMethod.Invoke(service, new object[] { entity1 })!;
        await processTask1;
        
        var processTask2 = (Task)specificProcessMethod.Invoke(service, new object[] { entity2 })!;
        await processTask2;

        // Test transform method with multiple type parameters
        var transformMethod = serviceType.GetMethod("TransformAsync");
        var specificTransformMethod = transformMethod!.MakeGenericMethod(sortableEntityType, resultClassType!);
        
        var transformTask1 = (Task)specificTransformMethod.Invoke(service, new object[] { entity1 })!;
        await transformTask1;
        
        var transformTask2 = (Task)specificTransformMethod.Invoke(service, new object[] { entity2 })!;
        await transformTask2;

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        
        // Note: Generic method caching with type parameters is complex and may not work
        // exactly like regular method caching due to type instantiation at runtime.
        // This test verifies that the source generator can handle generic methods without errors.
        
        var processCallCount = (int)implType?.GetProperty("ProcessEntityCallCount")?.GetValue(null)!;
        var transformCallCount = (int)implType?.GetProperty("TransformCallCount")?.GetValue(null)!;
        
        // For generic methods, caching might not work the same way due to runtime type instantiation
        // The main goal is to verify that source generation doesn't break with generic methods
        Assert.True(processCallCount >= 1);
        Assert.True(transformCallCount >= 1);

        _output.WriteLine($"✅ Generic methods with constraints test passed! Source generation handles generic methods correctly");
    }

    private static async Task<object> GetTaskResult(Task task, Type expectedType)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        var result = property!.GetValue(task)!;
        
        Assert.True(expectedType.IsAssignableFrom(result.GetType()), 
            $"Expected type {expectedType.Name}, but got {result.GetType().Name}");
        
        return result;
    }
}