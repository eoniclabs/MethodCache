using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Runtime;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

public class CacheKeyAttributeIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public CacheKeyAttributeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task CacheKeyAttribute_UsesRawKeyForCacheStorage()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public interface IRawKeyService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetValueAsync([CacheKey(UseAsRawKey = true)] string cacheKey);
    }

    public class RawKeyService : IRawKeyService
    {
        private static int _callCount = 0;

        public static void Reset() => _callCount = 0;
        public static int CallCount => _callCount;

        public async Task<string> GetValueAsync(string cacheKey)
        {
            _callCount++;
            await Task.Yield();
            return $""payload-{_callCount}"";
        }
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, logger: _output.WriteLine);

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IRawKeyService");
        Assert.NotNull(serviceType);
        var service = serviceProvider.GetRequiredService(serviceType);

        var implementationType = testAssembly.Assembly.GetType("TestNamespace.RawKeyService");
        Assert.NotNull(implementationType);
        implementationType!.GetMethod("Reset")?.Invoke(null, null);

        var method = serviceType!.GetMethod("GetValueAsync");
        Assert.NotNull(method);

        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var rawKey = "custom-cache-key";

        var firstCallTask = (Task)method!.Invoke(service, new object[] { rawKey })!;
        var firstResult = await GetTaskResult<string>(firstCallTask);

        // Verify the cache stored the value under the raw key
        var fastHit = await cacheManager.TryGetFastAsync<string>(rawKey);
        Assert.Equal(firstResult, fastHit);

        // The default ""MethodName:key"" format should not contain the cached value
        var prefixedHit = await cacheManager.TryGetFastAsync<string>($"GetValueAsync:{rawKey}");
        Assert.Null(prefixedHit);

        // Second call should hit cache and avoid incrementing call count
        var secondCallTask = (Task)method.Invoke(service, new object[] { rawKey })!;
        var secondResult = await GetTaskResult<string>(secondCallTask);
        Assert.Equal(firstResult, secondResult);

        var callCount = (int)(implementationType.GetProperty("CallCount")?.GetValue(null) ?? -1);
        Assert.Equal(1, callCount);
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task.ConfigureAwait(false);
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
    }
}
