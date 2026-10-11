using System;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Every cached method shape goes through the same path on the real in-memory cache: one lookup that knows
/// whether an entry exists, so null, 0 and false results are hits like any other value.
/// </summary>
public class SingleCachePathIntegrationTests
{
    private const string Source = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;

namespace SinglePath
{
    public enum Region { North = 1, South = 2 }

    public sealed record Filter(string Text, int Page);

    public interface IShapes
    {
        [Cache(Duration = ""00:10:00"")] Task<int> CountAsync(int id);
        [Cache(Duration = ""00:10:00"")] Task<bool> ExistsAsync(string name);
        [Cache(Duration = ""00:10:00"")] Task<string?> FindAsync(int id);
        [Cache(Duration = ""00:10:00"")] ValueTask<int> CountValueAsync(int id);
        [Cache(Duration = ""00:10:00"")] int Count(int id);
        [Cache(Duration = ""00:10:00"")] Task<string> ByRegionAsync(Region region);
        [Cache(Duration = ""00:10:00"")] Task<int> SearchAsync(Filter filter);
    }

    public class Shapes : IShapes
    {
        public static int Calls;
        public Task<int> CountAsync(int id) { Calls++; return Task.FromResult(0); }
        public Task<bool> ExistsAsync(string name) { Calls++; return Task.FromResult(false); }
        public Task<string?> FindAsync(int id) { Calls++; return Task.FromResult<string?>(null); }
        public ValueTask<int> CountValueAsync(int id) { Calls++; return new ValueTask<int>(0); }
        public int Count(int id) { Calls++; return 0; }
        public Task<string> ByRegionAsync(Region region) { Calls++; return Task.FromResult(region.ToString()); }
        public Task<int> SearchAsync(Filter filter) { Calls++; return Task.FromResult(filter.Page); }
    }
}";

    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine = new();

    public SingleCachePathIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("CountAsync", 1)]
    [InlineData("ExistsAsync", "name")]
    [InlineData("FindAsync", 1)]
    [InlineData("CountValueAsync", 1)]
    [InlineData("Count", 1)]
    public async Task DefaultAndNullResults_AreCachedHits(string method, object argument)
    {
        var (service, calls, _) = await BuildAsync();

        for (var i = 0; i < 3; i++)
        {
            await Invoke(service, method, argument);
        }

        Assert.Equal(1, (int)calls.GetValue(null)!);
    }

    [Fact]
    public async Task EnumArguments_UseTheInlineKey()
    {
        var (service, calls, assembly) = await BuildAsync();
        var region = Enum.ToObject(assembly.GetType("SinglePath.Region")!, 2);

        Assert.Equal("South", await Invoke(service, "ByRegionAsync", region));
        Assert.Equal("South", await Invoke(service, "ByRegionAsync", region));

        Assert.Equal(1, (int)calls.GetValue(null)!);
        var key = MethodCacheKeys.For(assembly.GetType("SinglePath.IShapes")!, "ByRegionAsync", "2");
        Assert.True(await (_provider!.GetRequiredService<ICacheManager>()).TryGetFastAsync<string>(key) == "South");
    }

    [Fact]
    public async Task ComplexArguments_HitOnEqualValues()
    {
        var (service, calls, assembly) = await BuildAsync();
        var filterType = assembly.GetType("SinglePath.Filter")!;

        Assert.Equal(3, await Invoke(service, "SearchAsync", Activator.CreateInstance(filterType, "a", 3)!));
        Assert.Equal(3, await Invoke(service, "SearchAsync", Activator.CreateInstance(filterType, "a", 3)!));

        Assert.Equal(1, (int)calls.GetValue(null)!);
    }

    private IServiceProvider? _provider;

    private async Task<(object Service, FieldInfo Calls, Assembly Assembly)> BuildAsync()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source);
        _provider = _engine.CreateTestServiceProvider(compiled, services =>
        {
            services.AddSingleton<ICacheManager>(sp => new InMemoryCacheManager(sp.GetRequiredService<ICacheMetricsProvider>(), null, null));
        }, _output.WriteLine);

        var assembly = compiled.Assembly;
        var calls = assembly.GetType("SinglePath.Shapes")!.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!;
        calls.SetValue(null, 0);
        return (_provider.GetRequiredService(assembly.GetType("SinglePath.IShapes")!), calls, assembly);
    }

    private static async Task<object?> Invoke(object service, string methodName, object argument)
    {
        var result = InterfaceMethods.Find(service, methodName).Invoke(service, new[] { argument });
        switch (result)
        {
            case Task task:
                await task.ConfigureAwait(false);
                return task.GetType().GetProperty("Result")!.GetValue(task);
            case ValueTask<int> valueTask:
                return await valueTask.ConfigureAwait(false);
            default:
                return result;
        }
    }
}
