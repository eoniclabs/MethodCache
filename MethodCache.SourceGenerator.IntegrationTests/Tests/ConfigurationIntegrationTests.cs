using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Integration tests for various cache configuration scenarios
/// </summary>
public class ConfigurationIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public ConfigurationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_CustomKeyGenerator_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace TestNamespace
{
    public class CustomKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, Type[] parameterTypes, object[] args)
        {
            return $""CUSTOM:{methodName}:{string.Join("","", args)}"";
        }
    }

    public interface ICustomKeyService
    {
        [Cache(Duration = ""00:02:00"", KeyGenerator = typeof(CustomKeyGenerator))]
        Task<User> GetUserAsync(int id, string name);
        
        [Cache(Duration = ""00:02:00"")] // Default key generator
        Task<User> GetUserByIdAsync(int id);
    }

    public class CustomKeyService : ICustomKeyService
    {
        private static int _customKeyCallCount = 0;
        private static int _defaultKeyCallCount = 0;
        
        public virtual async Task<User> GetUserAsync(int id, string name)
        {
            _customKeyCallCount++;
            await Task.Delay(10);
            return new User { Id = id, Name = name, Email = $""{name}@test.com"" };
        }
        
        public virtual async Task<User> GetUserByIdAsync(int id)
        {
            _defaultKeyCallCount++;
            await Task.Delay(10);
            return new User { Id = id, Name = $""User {id}"", Email = $""user{id}@test.com"" };
        }
        
        public static void ResetCallCounts()
        {
            _customKeyCallCount = 0;
            _defaultKeyCallCount = 0;
        }
        
        public static int CustomKeyCallCount => _customKeyCallCount;
        public static int DefaultKeyCallCount => _defaultKeyCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.ICustomKeyService");
        var service = serviceProvider.GetService(serviceType);
        var implType = testAssembly.Assembly.GetType("TestNamespace.CustomKeyService");
        
        Assert.NotNull(service);
        
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var customKeyMethod = serviceType!.GetMethod("GetUserAsync");
        var defaultKeyMethod = serviceType.GetMethod("GetUserByIdAsync");

        // Test custom key generation - same parameters should hit cache
        var user1Task = (Task)customKeyMethod!.Invoke(service, new object[] { 1, "John" })!;
        var user1 = await GetTaskResult<User>(user1Task);
        
        var user2Task = (Task)customKeyMethod.Invoke(service, new object[] { 1, "John" })!;
        var user2 = await GetTaskResult<User>(user2Task);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 1);
        Assert.Equal(1, metricsProvider.Metrics.HitCount);
        Assert.Equal(1, metricsProvider.Metrics.MissCount);

        // Different parameters should miss cache
        var user3Task = (Task)customKeyMethod.Invoke(service, new object[] { 1, "Jane" })!;
        var user3 = await GetTaskResult<User>(user3Task);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 2);
        Assert.Equal(2, metricsProvider.Metrics.MissCount);

        // Test default key generation
        var defaultUser1Task = (Task)defaultKeyMethod!.Invoke(service, new object[] { 1 })!;
        var defaultUser1 = await GetTaskResult<User>(defaultUser1Task);
        
        var defaultUser2Task = (Task)defaultKeyMethod.Invoke(service, new object[] { 1 })!;
        var defaultUser2 = await GetTaskResult<User>(defaultUser2Task);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 3);
        var finalMetrics = metricsProvider.Metrics;
        
        Assert.Equal(2, finalMetrics.HitCount);
        Assert.Equal(3, finalMetrics.MissCount);

        _output.WriteLine($"✅ Custom key generator test passed! Metrics - Hits: {finalMetrics.HitCount}, Misses: {finalMetrics.MissCount}");
    }

    [Fact]
    public async Task SourceGenerator_ConditionalCaching_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace TestNamespace
{
    public class ConditionalCacheCondition : ICacheCondition
    {
        public bool ShouldCache(object[] args, object result)
        {
            // Only cache if user is active
            return result is User user && user.IsActive;
        }
    }

    public interface IConditionalService
    {
        [Cache(Duration = ""00:02:00"", Condition = typeof(ConditionalCacheCondition))]
        Task<User> GetUserAsync(int id, bool isActive);
    }

    public class ConditionalService : IConditionalService
    {
        private static int _callCount = 0;
        
        public virtual async Task<User> GetUserAsync(int id, bool isActive)
        {
            _callCount++;
            await Task.Delay(10);
            return new User 
            { 
                Id = id, 
                Name = $""User {id}"", 
                Email = $""user{id}@test.com"",
                IsActive = isActive
            };
        }
        
        public static void ResetCallCount()
        {
            _callCount = 0;
        }
        
        public static int CallCount => _callCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IConditionalService");
        var service = serviceProvider.GetService(serviceType);
        var implType = testAssembly.Assembly.GetType("TestNamespace.ConditionalService");
        
        Assert.NotNull(service);
        
        implType?.GetMethod("ResetCallCount")?.Invoke(null, null);
        metricsProvider.Reset();

        var getUserMethod = serviceType!.GetMethod("GetUserAsync");

        // Test with active user - should be cached
        var activeUser1Task = (Task)getUserMethod!.Invoke(service, new object[] { 1, true })!;
        var activeUser1 = await GetTaskResult<User>(activeUser1Task);
        
        var activeUser2Task = (Task)getUserMethod.Invoke(service, new object[] { 1, true })!;
        var activeUser2 = await GetTaskResult<User>(activeUser2Task);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 1);
        Assert.Equal(1, metricsProvider.Metrics.HitCount);
        Assert.Equal(1, metricsProvider.Metrics.MissCount);

        // Test with inactive user - should NOT be cached (always miss)
        var inactiveUser1Task = (Task)getUserMethod.Invoke(service, new object[] { 2, false })!;
        var inactiveUser1 = await GetTaskResult<User>(inactiveUser1Task);
        
        var inactiveUser2Task = (Task)getUserMethod.Invoke(service, new object[] { 2, false })!;
        var inactiveUser2 = await GetTaskResult<User>(inactiveUser2Task);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 1, expectedMisses: 3);
        var finalMetrics = metricsProvider.Metrics;
        
        // Should have 1 hit and 3 misses (1 active + 2 inactive)
        Assert.Equal(1, finalMetrics.HitCount);
        Assert.Equal(3, finalMetrics.MissCount);

        // Verify call count - should be 4 total calls (1 active cached + 2 inactive not cached + 1 active hit)
        var callCount = (int)implType?.GetProperty("CallCount")?.GetValue(null)!;
        Assert.Equal(3, callCount); // 1 active + 2 inactive (second active was cached)

        _output.WriteLine($"✅ Conditional caching test passed! Only active users were cached");
    }

    [Fact]
    public async Task SourceGenerator_SerializationConfiguration_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace TestNamespace
{
    public interface ISerializationService
    {
        [Cache(Duration = ""00:02:00"", SerializationType = SerializationType.Json)]
        Task<User> GetUserJsonAsync(int id);
        
        [Cache(Duration = ""00:02:00"", SerializationType = SerializationType.MessagePack)]
        Task<User> GetUserMessagePackAsync(int id);
        
        [Cache(Duration = ""00:02:00"")] // Default serialization
        Task<User> GetUserDefaultAsync(int id);
    }

    public class SerializationService : ISerializationService
    {
        private static int _jsonCallCount = 0;
        private static int _messagePackCallCount = 0;
        private static int _defaultCallCount = 0;
        
        public virtual async Task<User> GetUserJsonAsync(int id)
        {
            _jsonCallCount++;
            await Task.Delay(5);
            return new User { Id = id, Name = $""JsonUser {id}"", Email = $""json{id}@test.com"", IsActive = true };
        }
        
        public virtual async Task<User> GetUserMessagePackAsync(int id)
        {
            _messagePackCallCount++;
            await Task.Delay(5);
            return new User { Id = id, Name = $""MsgPackUser {id}"", Email = $""msgpack{id}@test.com"", IsActive = true };
        }
        
        public virtual async Task<User> GetUserDefaultAsync(int id)
        {
            _defaultCallCount++;
            await Task.Delay(5);
            return new User { Id = id, Name = $""DefaultUser {id}"", Email = $""default{id}@test.com"", IsActive = true };
        }
        
        public static void ResetCallCounts()
        {
            _jsonCallCount = 0;
            _messagePackCallCount = 0;
            _defaultCallCount = 0;
        }
        
        public static int JsonCallCount => _jsonCallCount;
        public static int MessagePackCallCount => _messagePackCallCount;
        public static int DefaultCallCount => _defaultCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.ISerializationService");
        var service = serviceProvider.GetService(serviceType);
        var implType = testAssembly.Assembly.GetType("TestNamespace.SerializationService");
        
        Assert.NotNull(service);
        
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var jsonMethod = serviceType!.GetMethod("GetUserJsonAsync");
        var messagePackMethod = serviceType.GetMethod("GetUserMessagePackAsync");
        var defaultMethod = serviceType.GetMethod("GetUserDefaultAsync");

        // Test all serialization types with same ID - should create separate cache entries
        var jsonTask1 = (Task)jsonMethod!.Invoke(service, new object[] { 1 })!;
        var jsonUser1 = await GetTaskResult<User>(jsonTask1);
        
        var msgPackTask1 = (Task)messagePackMethod!.Invoke(service, new object[] { 1 })!;
        var msgPackUser1 = await GetTaskResult<User>(msgPackTask1);
        
        var defaultTask1 = (Task)defaultMethod!.Invoke(service, new object[] { 1 })!;
        var defaultUser1 = await GetTaskResult<User>(defaultTask1);

        await metricsProvider.WaitForMetricsAsync(expectedMisses: 3);
        Assert.Equal(3, metricsProvider.Metrics.MissCount);

        // Second calls should all be cache hits
        var jsonTask2 = (Task)jsonMethod.Invoke(service, new object[] { 1 })!;
        var jsonUser2 = await GetTaskResult<User>(jsonTask2);
        
        var msgPackTask2 = (Task)messagePackMethod.Invoke(service, new object[] { 1 })!;
        var msgPackUser2 = await GetTaskResult<User>(msgPackTask2);
        
        var defaultTask2 = (Task)defaultMethod.Invoke(service, new object[] { 1 })!;
        var defaultUser2 = await GetTaskResult<User>(defaultTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 3, expectedMisses: 3);
        var finalMetrics = metricsProvider.Metrics;
        
        Assert.Equal(3, finalMetrics.HitCount);
        Assert.Equal(3, finalMetrics.MissCount);

        // Verify each method was called once (cached on second call)
        var jsonCount = (int)implType?.GetProperty("JsonCallCount")?.GetValue(null)!;
        var msgPackCount = (int)implType?.GetProperty("MessagePackCallCount")?.GetValue(null)!;
        var defaultCount = (int)implType?.GetProperty("DefaultCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, jsonCount);
        Assert.Equal(1, msgPackCount);
        Assert.Equal(1, defaultCount);

        // Verify cached results are identical
        Assert.Equal(jsonUser1.Name, jsonUser2.Name);
        Assert.Equal(msgPackUser1.Name, msgPackUser2.Name);
        Assert.Equal(defaultUser1.Name, defaultUser2.Name);

        _output.WriteLine($"✅ Serialization configuration test passed! All serialization types worked correctly");
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
    }
}