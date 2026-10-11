using System.Globalization;
using System.Threading.Tasks;
using MethodCache;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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
using MethodCache;

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

    [Fact]
    public async Task CacheKeyAttribute_RawKeyFromNumber_IsCultureInvariant()
    {
        var sourceCode = @"
using System.Threading.Tasks;
using MethodCache;

namespace TestNamespace
{
    public interface IRawNumberKeyService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetByDoubleAsync([CacheKey(UseAsRawKey = true)] double value);

        [Cache(Duration = ""00:10:00"")]
        Task<string> GetByNullableAsync([CacheKey(UseAsRawKey = true)] decimal? value);

        [Cache(Duration = ""00:10:00"")]
        Task<string> GetByLevelAsync([CacheKey(UseAsRawKey = true)] Level value);

        [Cache(Duration = ""00:10:00"")]
        Task<string> GetByNullableLevelAsync([CacheKey(UseAsRawKey = true)] Level? value);

        [Cache(Duration = ""00:10:00"")]
        Task<string> GetByCodeAsync([CacheKey(UseAsRawKey = true)] Code value);
    }

    public enum Level { Low = -1, High = 1 }

    // Implements IFormattable explicitly, so it has no public ToString(format, provider)
    public readonly struct Code : System.IFormattable
    {
        public Code(int value) => Value = value;
        public int Value { get; }
        string System.IFormattable.ToString(string? format, System.IFormatProvider? provider) => ""code-"" + Value.ToString(provider);
    }

    public class RawNumberKeyService : IRawNumberKeyService
    {
        public Task<string> GetByDoubleAsync(double value) => Task.FromResult(""double"");

        public Task<string> GetByNullableAsync(decimal? value) => Task.FromResult(""decimal"");

        public Task<string> GetByLevelAsync(Level value) => Task.FromResult(""level "" + ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture));

        public Task<string> GetByNullableLevelAsync(Level? value) => Task.FromResult(""nullable level"");

        public Task<string> GetByCodeAsync(Code value) => Task.FromResult(""code"");
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, logger: _output.WriteLine);
        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IRawNumberKeyService")!;
        var service = serviceProvider.GetRequiredService(serviceType);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();

        var previous = CultureInfo.CurrentCulture;
        // sv-SE writes 1,5 and a U+2212 minus sign, so a culture-dependent key would differ per machine
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            await GetTaskResult<string>((Task)serviceType.GetMethod("GetByDoubleAsync")!.Invoke(service, new object[] { -1.5 })!);
            await GetTaskResult<string>((Task)serviceType.GetMethod("GetByNullableAsync")!.Invoke(service, new object?[] { 2.5m })!);

            var level = testAssembly.Assembly.GetType("TestNamespace.Level")!;
            await GetTaskResult<string>((Task)serviceType.GetMethod("GetByLevelAsync")!.Invoke(service, new[] { Enum.ToObject(level, -1) })!);
            await GetTaskResult<string>((Task)serviceType.GetMethod("GetByLevelAsync")!.Invoke(service, new[] { Enum.ToObject(level, -3) })!);
            await GetTaskResult<string>((Task)serviceType.GetMethod("GetByNullableLevelAsync")!.Invoke(service, new[] { Enum.ToObject(level, -5) })!);

            var code = Activator.CreateInstance(testAssembly.Assembly.GetType("TestNamespace.Code")!, -7)!;
            await GetTaskResult<string>((Task)serviceType.GetMethod("GetByCodeAsync")!.Invoke(service, new[] { code })!);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal("double", await cacheManager.TryGetFastAsync<string>("-1.5"));
        Assert.Equal("decimal", await cacheManager.TryGetFastAsync<string>("2.5"));

        // A named enum value keeps its name; one without a name is its number with an ASCII minus sign
        Assert.Equal("level -1", await cacheManager.TryGetFastAsync<string>("Low"));
        Assert.Equal("level -3", await cacheManager.TryGetFastAsync<string>("-3"));
        Assert.Equal("nullable level", await cacheManager.TryGetFastAsync<string>("-5"));
        Assert.Equal("code", await cacheManager.TryGetFastAsync<string>("code--7"));
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task.ConfigureAwait(false);
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
    }
}
