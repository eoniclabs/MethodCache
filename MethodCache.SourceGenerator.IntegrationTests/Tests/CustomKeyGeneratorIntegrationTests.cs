using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Integration tests for custom key generator scenarios with real source-generated code
/// </summary>
public class CustomKeyGeneratorIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public CustomKeyGeneratorIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_CustomKeyGenerator_Works()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;

namespace TestNamespace
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public class CustomUserKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
        {
            // Custom key format: METHOD:USER_{id}
            if (args.Length > 0 && args[0] is int userId)
            {
                return $""{methodName}:USER_{userId}"";
            }
            return $""{methodName}:{string.Join(""_"", args)}"";
        }
    }

    public class ComplexKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
        {
            // Complex key with method name, arg types, and values
            var keyParts = new System.Collections.Generic.List<string> { methodName };
            foreach (var arg in args)
            {
                if (arg != null)
                {
                    keyParts.Add($""{arg.GetType().Name}:{arg}"");
                }
                else
                {
                    keyParts.Add(""NULL"");
                }
            }
            return string.Join(""||"", keyParts);
        }
    }

    public interface ICustomKeyService
    {
        [Cache(Duration = ""00:02:00"", KeyGeneratorType = typeof(CustomUserKeyGenerator))]
        Task<User> GetUserWithCustomKeyAsync(int id);
        
        [Cache(Duration = ""00:02:00"", KeyGeneratorType = typeof(ComplexKeyGenerator))]
        Task<string> GetUserDataWithComplexKeyAsync(int id, string dataType);
        
        [Cache(Duration = ""00:02:00"")] // Default key generator
        Task<User> GetUserWithDefaultKeyAsync(int id);
    }

    public class CustomKeyService : ICustomKeyService
    {
        private static int _customKeyCallCount = 0;
        private static int _complexKeyCallCount = 0;
        private static int _defaultKeyCallCount = 0;
        
        public virtual async Task<User> GetUserWithCustomKeyAsync(int id)
        {
            _customKeyCallCount++;
            await Task.Delay(5);
            return new User { Id = id, Name = $""User {id}"", Email = $""user{id}@test.com"" };
        }
        
        public virtual async Task<string> GetUserDataWithComplexKeyAsync(int id, string dataType)
        {
            _complexKeyCallCount++;
            await Task.Delay(5);
            return $""Data for user {id}, type: {dataType}"";
        }
        
        public virtual async Task<User> GetUserWithDefaultKeyAsync(int id)
        {
            _defaultKeyCallCount++;
            await Task.Delay(5);
            return new User { Id = id, Name = $""Default User {id}"", Email = $""default{id}@test.com"" };
        }
        
        public static void ResetCallCounts()
        {
            _customKeyCallCount = 0;
            _complexKeyCallCount = 0;
            _defaultKeyCallCount = 0;
        }
        
        public static int CustomKeyCallCount => _customKeyCallCount;
        public static int ComplexKeyCallCount => _complexKeyCallCount;
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
        Assert.NotNull(service);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.CustomKeyService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        // Test custom key generator
        var customKeyMethod = serviceType!.GetMethod("GetUserWithCustomKeyAsync");
        var userType = testAssembly.Assembly.GetType("TestNamespace.User");
        Assert.NotNull(userType);

        // First call - should be cache miss
        var customTask1 = (Task)customKeyMethod!.Invoke(service, new object[] { 1 })!;
        var user1 = await GetTaskResult(customTask1, userType);
        
        // Second call with same parameters - should be cache hit
        var customTask2 = (Task)customKeyMethod.Invoke(service, new object[] { 1 })!;
        var user2 = await GetTaskResult(customTask2, userType);

        // Test complex key generator
        var complexKeyMethod = serviceType.GetMethod("GetUserDataWithComplexKeyAsync");
        var complexTask1 = (Task)complexKeyMethod!.Invoke(service, new object[] { 1, "profile" })!;
        var data1 = await GetTaskResult<string>(complexTask1);
        
        var complexTask2 = (Task)complexKeyMethod.Invoke(service, new object[] { 1, "profile" })!;
        var data2 = await GetTaskResult<string>(complexTask2);

        // Test default key generator
        var defaultKeyMethod = serviceType.GetMethod("GetUserWithDefaultKeyAsync");
        var defaultTask1 = (Task)defaultKeyMethod!.Invoke(service, new object[] { 1 })!;
        var defaultUser1 = await GetTaskResult(defaultTask1, userType);
        
        var defaultTask2 = (Task)defaultKeyMethod.Invoke(service, new object[] { 1 })!;
        var defaultUser2 = await GetTaskResult(defaultTask2, userType);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 3, expectedMisses: 3);
        
        // Verify each key generator worked independently
        var customCallCount = (int)implType?.GetProperty("CustomKeyCallCount")?.GetValue(null)!;
        var complexCallCount = (int)implType?.GetProperty("ComplexKeyCallCount")?.GetValue(null)!;
        var defaultCallCount = (int)implType?.GetProperty("DefaultKeyCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, customCallCount);
        Assert.Equal(1, complexCallCount);
        Assert.Equal(1, defaultCallCount);

        // Verify we got valid results
        Assert.NotNull(user1);
        Assert.NotNull(data1);
        Assert.NotNull(defaultUser1);

        _output.WriteLine($"✅ Custom key generator test passed! Different key generators work independently");
    }

    [Fact]
    public async Task SourceGenerator_KeyGeneratorWithDifferentParameters_Works()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;

namespace TestNamespace
{
    public class ParameterSensitiveKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
        {
            // Create keys that are sensitive to parameter order and types
            var parts = new System.Collections.Generic.List<string> { methodName };
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is string str)
                {
                    parts.Add($""STR{i}:{str.ToLowerInvariant()}"");
                }
                else if (arg is int intVal)
                {
                    parts.Add($""INT{i}:{intVal}"");
                }
                else if (arg is DateTime dt)
                {
                    parts.Add($""DT{i}:{dt:yyyy-MM-dd}"");
                }
                else
                {
                    parts.Add($""OBJ{i}:{arg?.ToString() ?? ""NULL""}"");
                }
            }
            return string.Join(""#"", parts);
        }
    }

    public interface IParameterTestService
    {
        [Cache(Duration = ""00:02:00"", KeyGeneratorType = typeof(ParameterSensitiveKeyGenerator))]
        Task<string> ProcessDataAsync(string category, int priority, DateTime timestamp);
        
        [Cache(Duration = ""00:02:00"", KeyGeneratorType = typeof(ParameterSensitiveKeyGenerator))]
        Task<string> ProcessDataReversedAsync(int priority, string category, DateTime timestamp);
    }

    public class ParameterTestService : IParameterTestService
    {
        private static int _processCallCount = 0;
        private static int _processReversedCallCount = 0;
        
        public virtual async Task<string> ProcessDataAsync(string category, int priority, DateTime timestamp)
        {
            _processCallCount++;
            await Task.Delay(5);
            return $""Processed: {category}-{priority}-{timestamp:yyyy-MM-dd}"";
        }
        
        public virtual async Task<string> ProcessDataReversedAsync(int priority, string category, DateTime timestamp)
        {
            _processReversedCallCount++;
            await Task.Delay(5);
            return $""Reversed: {priority}-{category}-{timestamp:yyyy-MM-dd}"";
        }
        
        public static void ResetCallCounts()
        {
            _processCallCount = 0;
            _processReversedCallCount = 0;
        }
        
        public static int ProcessCallCount => _processCallCount;
        public static int ProcessReversedCallCount => _processReversedCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IParameterTestService");
        var service = serviceProvider.GetService(serviceType);
        Assert.NotNull(service);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.ParameterTestService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var timestamp = new DateTime(2023, 1, 1);

        // Test that parameter order creates different cache keys
        var processMethod = serviceType!.GetMethod("ProcessDataAsync");
        var processReversedMethod = serviceType.GetMethod("ProcessDataReversedAsync");

        // Same values, different parameter order - should create different cache keys
        var task1 = (Task)processMethod!.Invoke(service, new object[] { "electronics", 5, timestamp })!;
        var result1 = await GetTaskResult<string>(task1);
        
        var task2 = (Task)processReversedMethod!.Invoke(service, new object[] { 5, "electronics", timestamp })!;
        var result2 = await GetTaskResult<string>(task2);

        // Call same methods again - should be cache hits
        var task3 = (Task)processMethod.Invoke(service, new object[] { "electronics", 5, timestamp })!;
        var result3 = await GetTaskResult<string>(task3);
        
        var task4 = (Task)processReversedMethod.Invoke(service, new object[] { 5, "electronics", timestamp })!;
        var result4 = await GetTaskResult<string>(task4);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        
        // Verify both methods were called once (proving different cache keys)
        var processCallCount = (int)implType?.GetProperty("ProcessCallCount")?.GetValue(null)!;
        var processReversedCallCount = (int)implType?.GetProperty("ProcessReversedCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, processCallCount);
        Assert.Equal(1, processReversedCallCount);

        // Verify results are different (proving different methods were called)
        Assert.NotEqual(result1, result2);
        Assert.Equal(result1, result3); // Same method, same result
        Assert.Equal(result2, result4); // Same method, same result

        _output.WriteLine($"✅ Parameter-sensitive key generator test passed! Parameter order creates different cache keys");
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
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