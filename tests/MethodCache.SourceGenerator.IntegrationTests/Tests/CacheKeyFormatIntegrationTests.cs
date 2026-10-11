using System;
using System.Linq;
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
using MethodCache.Core.Runtime;

namespace KeyFormat
{
    public sealed record Query(string Text, int Page);
    public sealed record Batch(int[] Ids);
    public record Range(string? From, int To);
    public record NamedRange(string? From, int To, string Name) : Range(From, To);
    public readonly record struct Point(int X, int Y);
    public sealed class Tenant : ICacheKeyProvider
    {
        public Tenant(string id) => Id = id;
        public string Id { get; }
        public string CacheKeyPart => Id;
    }
    public class Region : ICacheKeyProvider
    {
        public Region(string code) => Code = code;
        public string Code { get; }
        string ICacheKeyProvider.CacheKeyPart => Code;
    }
    public class SubRegion : Region, ICacheKeyProvider
    {
        public SubRegion(string code, string name) : base(code) => Name = name;
        public string Name { get; }
        string ICacheKeyProvider.CacheKeyPart => Code + ""/"" + Name;
    }

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

        [Cache(Duration = ""00:10:00"")]
        Task<int> SumAsync(Batch batch);

        [Cache(Duration = ""00:10:00"")]
        Task<string> RangeAsync(Range? range);

        [Cache(Duration = ""00:10:00"")]
        Task<string> PointAsync(Point point, Tenant tenant);

        [Cache(Duration = ""00:10:00"")]
        Task<string> RegionAsync(Region region);

        [Cache(Duration = ""00:10:00"")]
        Task<string> PolicyAsync(int policy);
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
        public Task<int> SumAsync(Batch batch) { Calls++; return Task.FromResult(batch.Ids.Length); }
        public Task<string> RangeAsync(Range? range) { Calls++; return Task.FromResult(""range-"" + Calls); }
        public Task<string> RegionAsync(Region region) { Calls++; return Task.FromResult(""region-"" + Calls); }
        public Task<string> PolicyAsync(int policy) { Calls++; return Task.FromResult(""policy-"" + policy); }
        public Task<string> PointAsync(Point point, Tenant tenant) { Calls++; return Task.FromResult(""point-"" + point.X + "","" + point.Y + ""-"" + tenant.Id); }
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

    [Fact]
    public async Task ParametersNamedLikeGeneratedLocals_Compile()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();

        Assert.Equal("policy-5", await Call<string>(users, "PolicyAsync", 5));
        Assert.Equal("policy-5", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(assembly.GetType("KeyFormat.IUserService")!, "PolicyAsync", MethodCacheKeys.Part(5))));
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
    public async Task RecordStructAndKeyProviderArguments_GetReadableInlineKeys()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var userType = assembly.GetType("KeyFormat.IUserService")!;

        var query = Activator.CreateInstance(assembly.GetType("KeyFormat.Query")!, "a:b", 4)!;
        var point = Activator.CreateInstance(assembly.GetType("KeyFormat.Point")!, 1, 2)!;
        var tenant = Activator.CreateInstance(assembly.GetType("KeyFormat.Tenant")!, "acme")!;

        Assert.Equal(4, await Call<int>(users, "CountAsync", query));
        Assert.Equal("point-1,2-acme", await Call<string>(users, "PointAsync", point, tenant));

        Assert.Equal(4, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "CountAsync", "(a\\:b:4)")));
        Assert.Equal("point-1,2-acme", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(userType, "PointAsync", "(1:2)", "acme")));
    }

    [Fact]
    public async Task KeyProviders_UseTheInterfaceImplementation()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var userType = assembly.GetType("KeyFormat.IUserService")!;

        var region = await Call<string>(users, "RegionAsync", Activator.CreateInstance(assembly.GetType("KeyFormat.Region")!, "se"));
        var subRegion = await Call<string>(users, "RegionAsync", Activator.CreateInstance(assembly.GetType("KeyFormat.SubRegion")!, "se", "north"));

        Assert.NotEqual(region, subRegion);
        Assert.Equal(region, await cache.TryGetFastAsync<string>(MethodCacheKeys.For(userType, "RegionAsync", "se")));
        Assert.Equal(subRegion, await cache.TryGetFastAsync<string>(MethodCacheKeys.For(userType, "RegionAsync", "se/north")));
    }

    [Fact]
    public async Task EqualRecords_ShareAnEntry_AndNullOrSubclassesDoNot()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var rangeType = assembly.GetType("KeyFormat.Range")!;
        var namedRangeType = assembly.GetType("KeyFormat.NamedRange")!;

        var first = await Call<string>(users, "RangeAsync", Activator.CreateInstance(rangeType, null, 0));
        var equal = await Call<string>(users, "RangeAsync", Activator.CreateInstance(rangeType, null, 0));
        var nullRange = await Call<string>(users, "RangeAsync", new object?[] { null });
        var subclass = await Call<string>(users, "RangeAsync", Activator.CreateInstance(namedRangeType, null, 0, "x"));
        var otherSubclass = await Call<string>(users, "RangeAsync", Activator.CreateInstance(namedRangeType, null, 0, "y"));

        Assert.Equal(first, equal);
        Assert.Equal(4, new[] { first, nullRange, subclass, otherSubclass }.Distinct().Count());
    }

    [Fact]
    public async Task ArgumentsWithoutAnInlineFormat_UseHashedKeysWithTheServiceAndMethodPrefix()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var batch = Activator.CreateInstance(assembly.GetType("KeyFormat.Batch")!, new object[] { new[] { 1, 2, 3 } })!;

        Assert.Equal(3, await Call<int>(users, "SumAsync", batch));

        var prefix = MethodCacheKeys.Prefix(assembly.GetType("KeyFormat.IUserService")!, "SumAsync");
        var policy = CacheRuntimePolicy.FromPolicy("SumAsync", CachePolicy.Empty, CachePolicyFields.None);
        var key = provider.GetRequiredService<ICacheKeyGenerator>().GenerateKey(prefix, new[] { batch }, policy);

        Assert.StartsWith(prefix + ":", key);
        Assert.Equal(3, await cache.TryGetFastAsync<int>(key));
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
        var task = (Task)InterfaceMethods.Find(service, methodName).Invoke(service, args)!;
        await task.ConfigureAwait(false);
        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
