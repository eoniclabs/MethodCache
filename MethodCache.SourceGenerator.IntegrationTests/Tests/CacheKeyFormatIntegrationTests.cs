using System;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

public class CacheKeyFormatIntegrationTests
{
    private const string Source = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;

namespace KeyFormat
{
    public sealed record Query(string Text, int Page);

    public interface IUserService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetAsync(int id);

        [Cache(Duration = ""00:10:00"")]
        Task<string> FindAsync(string? first, string? second);

        [Cache(Duration = ""00:10:00"")]
        Task<string> AllAsync();

        [Cache(Duration = ""00:10:00"")]
        Task<int> CountAsync(Query query);
    }

    public interface IOrderService
    {
        [Cache(Duration = ""00:10:00"", Version = 2)]
        Task<string> GetAsync(int id);
    }

    public class UserService : IUserService
    {
        public static int Calls;
        public Task<string> GetAsync(int id) { Calls++; return Task.FromResult(""user-"" + id); }
        public Task<string> FindAsync(string? first, string? second) { Calls++; return Task.FromResult(""find-"" + Calls); }
        public Task<string> AllAsync() { Calls++; return Task.FromResult(""all""); }
        public Task<int> CountAsync(Query query) { Calls++; return Task.FromResult(query.Page); }
    }

    public class OrderService : IOrderService
    {
        public Task<string> GetAsync(int id) => Task.FromResult(""order-"" + id);
    }
}";

    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine = new();

    public CacheKeyFormatIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SameMethodAndArgumentOnTwoInterfaces_AreCachedSeparately()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var orders = Resolve(provider, assembly, "KeyFormat.IOrderService");

        Assert.Equal("user-1", await Call<string>(users, "GetAsync", 1));
        Assert.Equal("order-1", await Call<string>(orders, "GetAsync", 1));
        Assert.Equal("user-1", await Call<string>(users, "GetAsync", 1));
    }

    [Fact]
    public async Task InlineKeys_MatchMethodCacheKeys()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var orders = Resolve(provider, assembly, "KeyFormat.IOrderService");
        var cache = provider.GetRequiredService<ICacheManager>();

        await Call<string>(users, "GetAsync", 7);
        await Call<string>(orders, "GetAsync", 7);
        await Call<string>(users, "AllAsync");

        var userType = assembly.GetType("KeyFormat.IUserService")!;
        var orderType = assembly.GetType("KeyFormat.IOrderService")!;
        Assert.Equal("user-7", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(userType, "GetAsync", MethodCacheKeys.Part(7))));
        Assert.Equal("order-7", await cache.TryGetFastAsync<string>(MethodCacheKeys.ForVersion(orderType, "GetAsync", 2, MethodCacheKeys.Part(7))));
        Assert.Equal("all", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(userType, "AllAsync")));
    }

    [Fact]
    public async Task InvalidatingTheDocumentedKey_RemovesTheEntry()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var calls = assembly.GetType("KeyFormat.UserService")!.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!;
        calls.SetValue(null, 0);

        await Call<string>(users, "GetAsync", 3);
        await cache.InvalidateByKeysAsync(MethodCacheKeys.For(assembly.GetType("KeyFormat.IUserService")!, "GetAsync", MethodCacheKeys.Part(3)));
        await Call<string>(users, "GetAsync", 3);

        Assert.Equal(2, (int)calls.GetValue(null)!);
    }

    [Theory]
    [InlineData("a:b", "c", "a", "b:c")]
    [InlineData(null, "x", "null", "x")]
    public async Task StringArgumentsThatOnlyLookAlike_DoNotShareAnEntry(string? a1, string? a2, string? b1, string? b2)
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");

        var first = await Call<string>(users, "FindAsync", a1, a2);
        var second = await Call<string>(users, "FindAsync", b1, b2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task HashedKeys_KeepTheServiceAndMethodPrefix()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var queryType = assembly.GetType("KeyFormat.Query")!;
        var query = Activator.CreateInstance(queryType, "text", 4)!;

        Assert.Equal(4, await Call<int>(users, "CountAsync", query));

        var prefix = MethodCacheKeys.Prefix(assembly.GetType("KeyFormat.IUserService")!, "CountAsync");
        var policy = CacheRuntimePolicy.FromPolicy("CountAsync", CachePolicy.Empty, CachePolicyFields.None);
        var key = provider.GetRequiredService<ICacheKeyGenerator>().GenerateKey(prefix, new[] { query }, policy);

        Assert.StartsWith(prefix + ":", key);
        Assert.Equal(4, await cache.TryGetFastAsync<int>(key));
    }

    private async Task<(IServiceProvider Provider, Assembly Assembly)> BuildAsync()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source);
        var provider = _engine.CreateTestServiceProvider(compiled, logger: _output.WriteLine);
        return (provider, compiled.Assembly);
    }

    private static object Resolve(IServiceProvider provider, Assembly assembly, string typeName)
        => provider.GetRequiredService(assembly.GetType(typeName)!);

    private static async Task<T> Call<T>(object service, string methodName, params object?[] args)
    {
        var task = (Task)service.GetType().GetMethod(methodName)!.Invoke(service, args)!;
        await task.ConfigureAwait(false);
        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
