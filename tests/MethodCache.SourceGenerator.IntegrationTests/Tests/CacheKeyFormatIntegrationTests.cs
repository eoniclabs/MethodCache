using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
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
using MethodCache;

namespace KeyFormat
{
    public sealed record Query(string Text, int Page);
    public sealed record Batch(int[] Ids);
    public record Range(string? From, int To);
    public record NamedRange(string? From, int To, string Name) : Range(From, To);
    public readonly record struct Point(int X, int Y);
    public sealed class IdList : System.Collections.Generic.List<int> { }
    public interface IScope : ICacheKeyProvider { }
    public sealed class Scope : IScope
    {
        public Scope(string name) => Name = name;
        public string Name { get; }
        public string CacheKeyPart => Name;
    }
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

        [Cache(Duration = ""00:10:00"")]
        Task<int> IdsAsync(int[]? ids);

        [Cache(Duration = ""00:10:00"")]
        Task<int> NamesAsync(System.Collections.Generic.IReadOnlyList<string?> names, int page);

        [Cache(Duration = ""00:10:00"")]
        Task<int> PointsAsync(System.Collections.Generic.List<Point> points, System.Collections.Generic.IEnumerable<Tenant?> tenants);

        [Cache(Duration = ""00:10:00"")]
        Task<int> QueriesAsync(Query[] queries);

        [Cache(Duration = ""00:10:00"")]
        Task<int> FiltersAsync(System.Collections.Generic.Dictionary<string, int> filters);

        [Cache(Duration = ""00:10:00"")]
        Task<int> RangesAsync(Range[] ranges);

        [Cache(Duration = ""00:10:00"")]
        Task<int> SequenceAsync(System.Collections.Generic.IEnumerable<int> values);

        [Cache(Duration = ""00:10:00"")]
        Task<int> ListAsync(System.Collections.Generic.List<int> values);

        [Cache(Duration = ""00:10:00"")]
        Task<int> ScopesAsync(IScope[] scopes, ICacheKeyProvider? single);
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
        public Task<int> IdsAsync(int[]? ids) { Calls++; return Task.FromResult(Calls); }
        public Task<int> NamesAsync(System.Collections.Generic.IReadOnlyList<string?> names, int page) { Calls++; return Task.FromResult(Calls); }
        public Task<int> PointsAsync(System.Collections.Generic.List<Point> points, System.Collections.Generic.IEnumerable<Tenant?> tenants) { Calls++; return Task.FromResult(Calls); }
        public Task<int> QueriesAsync(Query[] queries) { Calls++; return Task.FromResult(Calls); }
        public Task<int> FiltersAsync(System.Collections.Generic.Dictionary<string, int> filters) { Calls++; return Task.FromResult(Calls); }
        public Task<int> RangesAsync(Range[] ranges) { Calls++; return Task.FromResult(Calls); }
        public Task<int> SequenceAsync(System.Collections.Generic.IEnumerable<int> values) { Calls++; return Task.FromResult(Calls); }
        public Task<int> ListAsync(System.Collections.Generic.List<int> values) { Calls++; return Task.FromResult(Calls); }
        public Task<int> ScopesAsync(IScope[] scopes, ICacheKeyProvider? single) { Calls++; return Task.FromResult(Calls); }
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

    [Fact]
    public async Task CollectionArguments_GetReadableInlineKeys()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var userType = assembly.GetType("KeyFormat.IUserService")!;

        var ids = await Call<int>(users, "IdsAsync", new[] { 1, 2, 3 });
        var noIds = await Call<int>(users, "IdsAsync", new object?[] { null });
        var names = await Call<int>(users, "NamesAsync", new List<string?> { "a:b", null }, 2);

        var pointType = assembly.GetType("KeyFormat.Point")!;
        var points = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pointType))!;
        points.Add(Activator.CreateInstance(pointType, 1, 2));
        var tenantType = assembly.GetType("KeyFormat.Tenant")!;
        var tenants = Array.CreateInstance(tenantType, 2);
        tenants.SetValue(Activator.CreateInstance(tenantType, "acme"), 0);
        var pointsResult = await Call<int>(users, "PointsAsync", points, tenants);

        var queryType = assembly.GetType("KeyFormat.Query")!;
        var queries = Array.CreateInstance(queryType, 1);
        queries.SetValue(Activator.CreateInstance(queryType, "x", 1), 0);
        var queriesResult = await Call<int>(users, "QueriesAsync", queries);
        var filters = await Call<int>(users, "FiltersAsync", new Dictionary<string, int> { ["a"] = 1 });

        Assert.Equal(ids, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "IdsAsync", "[3:1:2:3]")));
        Assert.Equal(noIds, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "IdsAsync", MethodCacheKeys.Null)));
        Assert.Equal(names, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "NamesAsync", "System.Collections.Generic.List`1[System.String]", "[2:a\\\\\\:b:\\\\0]", "2")));
        Assert.Equal(pointsResult, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "PointsAsync", "[1:(1\\:2)]", "KeyFormat.Tenant[]", "[2:acme:\\\\0]")));
        Assert.Equal(queriesResult, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "QueriesAsync", "[1:(x\\:1)]")));
        Assert.Equal(filters, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "FiltersAsync", "[1:(a\\:1)]")));
    }

    [Fact]
    public async Task EqualCollections_ShareAnEntry_AndDifferentOnesDoNot()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");

        var first = await Call<int>(users, "IdsAsync", new[] { 1, 2 });
        var equal = await Call<int>(users, "IdsAsync", new[] { 1, 2 });
        var reordered = await Call<int>(users, "IdsAsync", new[] { 2, 1 });
        var empty = await Call<int>(users, "IdsAsync", Array.Empty<int>());
        var emptyName = await Call<int>(users, "NamesAsync", new List<string?> { "" }, 1);
        var noNames = await Call<int>(users, "NamesAsync", new List<string?>(), 1);

        Assert.Equal(first, equal);
        Assert.Equal(5, new[] { first, reordered, empty, emptyName, noNames }.Distinct().Count());
    }

    [Fact]
    public async Task CollectionsDeclaredAsInterfaces_KeepTheirRuntimeTypeApart()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");

        var array = await Call<int>(users, "SequenceAsync", new[] { 1, 2 });
        var sameArray = await Call<int>(users, "SequenceAsync", new[] { 1, 2 });
        var list = await Call<int>(users, "SequenceAsync", new List<int> { 1, 2 });

        Assert.Equal(array, sameArray);
        Assert.NotEqual(array, list);

        // Declared as List<int>: a plain list keeps the short key; an unknown subclass is hashed with its runtime state.
        var cache = provider.GetRequiredService<ICacheManager>();
        var userType = assembly.GetType("KeyFormat.IUserService")!;
        var plain = await Call<int>(users, "ListAsync", new List<int> { 1, 2 });
        var subclass = (System.Collections.IList)Activator.CreateInstance(assembly.GetType("KeyFormat.IdList")!)!;
        subclass.Add(1);
        subclass.Add(2);
        var derived = await Call<int>(users, "ListAsync", subclass);

        Assert.NotEqual(plain, derived);
        Assert.Equal(plain, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "ListAsync", "[2:1:2]")));
        var derivedKey = provider.GetRequiredService<MethodCache.Extensibility.ICacheKeyGenerator>().GenerateKey(
            MethodCacheKeys.Prefix(userType, "ListAsync"), [subclass], CacheRuntimePolicy.Empty("ListAsync"));
        Assert.Equal(derived, await cache.TryGetFastAsync<int>(derivedKey));
    }

    [Fact]
    public async Task KeyProviderInterfaces_AreWrittenInline_AlsoAsElements()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var userType = assembly.GetType("KeyFormat.IUserService")!;
        var scopeType = assembly.GetType("KeyFormat.Scope")!;
        var scopes = Array.CreateInstance(assembly.GetType("KeyFormat.IScope")!, 2);
        scopes.SetValue(Activator.CreateInstance(scopeType, "eu"), 0);

        var result = await Call<int>(users, "ScopesAsync", scopes, Activator.CreateInstance(scopeType, "us"));

        Assert.Equal(result, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "ScopesAsync", "[2:eu:\\\\0]", "us")));
    }

    [Fact]
    public async Task CollectionsOfUnsealedRecords_UseTheKeyGenerator()
    {
        var (provider, assembly) = await BuildAsync();
        var users = Resolve(provider, assembly, "KeyFormat.IUserService");
        var cache = provider.GetRequiredService<ICacheManager>();
        var userType = assembly.GetType("KeyFormat.IUserService")!;
        var rangeType = assembly.GetType("KeyFormat.Range")!;
        var ranges = Array.CreateInstance(rangeType, 1);
        ranges.SetValue(Activator.CreateInstance(rangeType, "a", 1), 0);

        var result = await Call<int>(users, "RangesAsync", ranges);

        // A Range[] can hold a NamedRange, whose extra member an inline key would miss
        Assert.Equal(0, await cache.TryGetFastAsync<int>(MethodCacheKeys.For(userType, "RangesAsync", "[1:(a\\:1)]")));
        var prefix = MethodCacheKeys.Prefix(userType, "RangesAsync");
        var policy = CacheRuntimePolicy.FromPolicy("RangesAsync", CachePolicy.Empty, CachePolicyFields.None);
        var key = provider.GetRequiredService<ICacheKeyGenerator>().GenerateKey(prefix, new object[] { ranges }, policy);
        Assert.Equal(result, await cache.TryGetFastAsync<int>(key));
    }

    [Fact]
    public async Task Dates_AreWrittenLikeGeneratedKeys_SoUtcAndLocalOfOneInstantShareAnEntry()
    {
        const string source = @"
using System;
using System.Threading.Tasks;
using MethodCache;

namespace KeyFormat
{
    public interface IReports
    {
        [Cache] Task<string> At(DateTime at);
        [Cache] Task<string> Since(DateTimeOffset? since);
    }
    public class Reports : IReports
    {
        public static int AtCalls;
        public Task<string> At(DateTime at) { AtCalls++; return Task.FromResult(""at""); }
        public Task<string> Since(DateTimeOffset? since) => Task.FromResult(""since"");
    }
}";
        var compiled = await _engine.CompileWithSourceGeneratorAsync(source, "KeyFormatRawDates");
        var provider = _engine.CreateTestServiceProvider(compiled, logger: _output.WriteLine);
        var cache = provider.GetRequiredService<ICacheManager>();
        var reportsType = compiled.Assembly.GetType("KeyFormat.IReports")!;
        var reports = provider.GetRequiredService(reportsType);
        var utc = new DateTime(2026, 1, 1, 12, 0, 0, 250, DateTimeKind.Utc);
        var since = new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.FromHours(2));

        await (Task<string>)reportsType.GetMethod("At")!.Invoke(reports, new object[] { utc })!;
        await (Task<string>)reportsType.GetMethod("At")!.Invoke(reports, new object[] { utc.ToLocalTime() })!;
        await (Task<string>)reportsType.GetMethod("Since")!.Invoke(reports, new object?[] { since })!;

        // The local time of the same instant was a hit, under the key MethodCacheKeys.Part writes, whatever the time zone
        Assert.Equal(1, (int)compiled.Assembly.GetType("KeyFormat.Reports")!.GetField("AtCalls")!.GetValue(null)!);
        Assert.Equal("at", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(reportsType, "At", MethodCacheKeys.Part(utc))));
        Assert.Equal("at", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(reportsType, "At", "2026-01-01T12:00:00.2500000Z")));
        Assert.Equal("since", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(reportsType, "Since", MethodCacheKeys.Part(since))));
    }

    [Fact]
    public async Task MethodNamesTwoInterfacesOfAHierarchyDeclare_GetTheDocumentedKeys()
    {
        const string source = @"
using System.Threading.Tasks;
using MethodCache;

namespace KeyFormat
{
    public interface ILookup { [Cache] Task<string> Find(int id); }
    public interface ICatalog : ILookup { [Cache] Task<string> Find(string code); }
    public class Catalog : ICatalog
    {
        public Task<string> Find(int id) => Task.FromResult(""id"" + id);
        public Task<string> Find(string code) => Task.FromResult(""code"" + code);
    }

    public interface IA { [Cache] Task<string> Get(int id); }
    public interface IB { [Cache] Task<string> Get(int id); }
    public interface IC : IA, IB { }
    public class Both : IC
    {
        Task<string> IA.Get(int id) => Task.FromResult(""a"" + id);
        Task<string> IB.Get(int id) => Task.FromResult(""b"" + id);
    }
}";
        var compiled = await _engine.CompileWithSourceGeneratorAsync(source, "KeyFormatHierarchy");
        var provider = _engine.CreateTestServiceProvider(compiled, logger: _output.WriteLine);
        var cache = provider.GetRequiredService<ICacheManager>();
        Type Type(string name) => compiled.Assembly.GetType("KeyFormat." + name)!;

        var catalog = provider.GetRequiredService(Type("ICatalog"));
        await (Task<string>)Type("ILookup").GetMethod("Find")!.Invoke(catalog, new object[] { 7 })!;
        await (Task<string>)Type("ICatalog").GetMethod("Find")!.Invoke(catalog, new object[] { "7" })!;
        var both = provider.GetRequiredService(Type("IC"));
        await (Task<string>)Type("IA").GetMethod("Get")!.Invoke(both, new object[] { 1 })!;
        await (Task<string>)Type("IB").GetMethod("Get")!.Invoke(both, new object[] { 1 })!;

        // The keys docs/features/keys.md shows
        Assert.Equal("id7", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(Type("ICatalog"), "Find(int)", MethodCacheKeys.Part(7))));
        Assert.Equal("code7", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(Type("ICatalog"), "Find(string)", MethodCacheKeys.Part("7"))));
        Assert.Equal("a1", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(Type("IC"), "[KeyFormatHierarchy]KeyFormat.IA.Get(int)", MethodCacheKeys.Part(1))));
        Assert.Equal("b1", await cache.TryGetFastAsync<string>(MethodCacheKeys.For(Type("IC"), "[KeyFormatHierarchy]KeyFormat.IB.Get(int)", MethodCacheKeys.Part(1))));
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
