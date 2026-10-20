using MethodCache.Core.Management;
using MethodCache.Core.Runtime.Http;
using MethodCache.Core.Telemetry;
using MethodCache.Core.Tests.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Management;

/// <summary>
/// One method's entries through the administrator: <see cref="IMethodCacheKeys.MethodId{TService}"/>,
/// <see cref="CacheEntryInfo.MethodId"/>, <c>ListEntries(methodId:)</c> and <see cref="ICacheAdministrator.InvalidateMethodAsync"/>.
/// </summary>
public class MethodAdministrationTests
{
    private const string Ids = "[MethodCache.Core.Tests]MethodCache.Core.Tests.Management.MethodAdministrationTests.IUsers.";

    public interface IUsers
    {
        [Cache] Task<string> GetUser(int id);
        [Cache] Task<string> GetUsers(int page);
        [Cache] Task<string> Find(int id);
        [Cache] Task<string> Find(string name);
        [Cache] Task<string?> Load<T>(int id);
    }

    public sealed class Users : IUsers
    {
        public int Loads;
        public Task<string> GetUser(int id) => Load("user-" + id);
        public Task<string> GetUsers(int page) => Load("page-" + page);
        public Task<string> Find(int id) => Load("id-" + id);
        public Task<string> Find(string name) => Load("name-" + name);
        Task<string?> IUsers.Load<T>(int id) => Load<string?>(typeof(T).Name + id);
        private Task<TValue> Load<TValue>(TValue value) { Interlocked.Increment(ref Loads); return Task.FromResult(value); }
    }

    public interface IRepository<T>
    {
        [Cache] Task<int> CountAsync(int id);
    }

    public sealed class Numbers : IRepository<int>
    {
        public Task<int> CountAsync(int id) => Task.FromResult(id);
    }

    public sealed class Names : IRepository<string>
    {
        public Task<int> CountAsync(int id) => Task.FromResult(id);
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Users>();
        services.AddSingleton<IUsers>(sp => sp.GetRequiredService<Users>());
        services.AddSingleton<IRepository<int>>(new Numbers());
        services.AddKeyedSingleton<IRepository<string>>("a", new Names());
        services.AddKeyedSingleton<IRepository<string>>("b", new Names());
        configure?.Invoke(services);
        services.AddMethodCache(builder => builder.FromAssemblyOf<MethodAdministrationTests>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task MethodId_IsTheRuntimeIdOfTheCalledMethod_ForEveryKindOfMethod()
    {
        await using var provider = Build();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();
        var methods = provider.GetRequiredService<ICacheAdministrator>().GetMethods().Select(method => method.MethodId).ToHashSet();

        var getUser = keys.MethodId<IUsers>(users => users.GetUser(0));
        var findById = keys.MethodId<IUsers>(users => users.Find(0));
        var findByName = keys.MethodId<IUsers>(users => users.Find(""));
        var load = keys.MethodId<IUsers>(users => users.Load<int>(0));
        var count = keys.MethodId<IRepository<int>>(repository => repository.CountAsync(0));

        Assert.Equal(Ids + "GetUser", getUser);
        Assert.Equal(Ids + "Find(int)", findById);
        Assert.Equal(Ids + "Find(string)", findByName);
        // One id for every type argument of a generic method or interface, and for every keyed registration
        Assert.Equal(load, keys.MethodId<IUsers>(users => users.Load<string>(0)));
        Assert.Equal(count, keys.MethodId<IRepository<string>>(repository => repository.CountAsync(0), "a"));
        Assert.Equal(count, keys.MethodId<IRepository<string>>(repository => repository.CountAsync(0), "b"));
        Assert.All(new[] { getUser, findById, findByName, load, count }, id => Assert.Contains(id, methods));
        Assert.Equal(0, provider.GetRequiredService<Users>().Loads);
    }

    [Fact]
    public async Task MethodId_RejectsCallsThatAreNotOneCachedMethod()
    {
        await using var provider = Build();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();

        var none = Assert.Throws<InvalidOperationException>(() => keys.MethodId<IUsers>(_ => null));
        Assert.Contains("IMethodCacheKeys.MethodId<IUsers>", none.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => keys.MethodId<IUsers>(users => (users.GetUser(1), users.GetUsers(1))));
        // The keyed registrations have no unkeyed sibling
        Assert.Throws<InvalidOperationException>(() => keys.MethodId<IRepository<string>>(repository => repository.CountAsync(0)));
        Assert.Throws<ArgumentNullException>(() => keys.MethodId<IUsers>(null!));
    }

    [Fact]
    public async Task EntryMethodId_NamesTheMethod_AndIsNullForManualEntries()
    {
        await using var provider = Build();
        var users = provider.GetRequiredService<IUsers>();
        var cache = provider.GetRequiredService<ICacheManager>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();
        await users.GetUser(1);
        await users.Load<int>(1);
        await provider.GetRequiredKeyedService<IRepository<string>>("a").CountAsync(1);
        await cache.GetOrCreateAsync("manual", _ => ValueTask.FromResult(1));
        await cache.GetOrCreateManyAsync<int>(["bulk"], (missing, _) => ValueTask.FromResult<IReadOnlyDictionary<string, int>>(
            missing.ToDictionary(key => key, _ => 2)));

        Assert.Equal(Ids + "GetUser", admin.GetEntry(keys.For<IUsers>(u => u.GetUser(1)))!.MethodId);
        Assert.Equal(keys.MethodId<IUsers>(u => u.Load<int>(1)), admin.GetEntry(keys.For<IUsers>(u => u.Load<int>(1)))!.MethodId);
        Assert.Equal(keys.MethodId<IRepository<string>>(r => r.CountAsync(1), "a"),
            admin.GetEntry(keys.For<IRepository<string>>(r => r.CountAsync(1), "a"))!.MethodId);
        Assert.Null(admin.GetEntry("manual")!.MethodId);
        Assert.Null(admin.GetEntry("bulk")!.MethodId);
        Assert.Equal(["bulk", "manual"], admin.ListEntries().Where(entry => entry.MethodId == null).Select(entry => entry.StoredKey));
    }

    [Fact]
    public async Task ListEntries_ByMethodId_MatchesTheIdExactly()
    {
        await using var provider = Build();
        var users = provider.GetRequiredService<IUsers>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();
        await users.GetUser(1);
        await users.GetUser(2);
        await users.GetUsers(1);
        await users.Find(1);
        await users.Find("ann");
        await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync("manual", _ => ValueTask.FromResult(1));

        var getUser = admin.ListEntries(methodId: Ids + "GetUser");
        Assert.Equal([keys.For<IUsers>(u => u.GetUser(1)), keys.For<IUsers>(u => u.GetUser(2))], getUser.Select(entry => entry.StoredKey));
        // A stored-key prefix also matches the method whose name extends it; the method id does not
        var first = keys.For<IUsers>(u => u.GetUser(1));
        Assert.Equal(3, admin.ListEntries(storedKeyPrefix: first[..first.LastIndexOf(':')]).Count);
        Assert.Single(admin.ListEntries(methodId: Ids + "Find(string)"));
        Assert.Empty(admin.ListEntries(methodId: Ids + "Find"));
        Assert.Empty(admin.ListEntries(methodId: "MethodCache.Fluent"));
        Assert.Single(admin.ListEntries(methodId: Ids + "GetUser", limit: 1));
        Assert.Empty(admin.ListEntries(storedKeyPrefix: keys.For<IUsers>(u => u.GetUsers(1)), methodId: Ids + "GetUser"));
    }

    [Fact]
    public async Task InvalidateMethodAsync_RemovesEveryResultOfThatMethodOnly()
    {
        await using var provider = Build();
        var users = provider.GetRequiredService<IUsers>();
        var counter = provider.GetRequiredService<Users>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();
        var a = provider.GetRequiredKeyedService<IRepository<string>>("a");
        await users.GetUser(1);
        await users.GetUser(2);
        await users.GetUsers(1);
        await users.Load<int>(1);
        await users.Load<string>(1);
        await a.CountAsync(1);
        await provider.GetRequiredService<IRepository<int>>().CountAsync(1);
        await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync("manual", _ => ValueTask.FromResult(1));

        var result = await admin.InvalidateMethodAsync(Ids + "GetUser");
        Assert.Equal(new InvalidationResult { Requested = 1, Local = 1 }, result);
        Assert.Empty(admin.ListEntries(methodId: Ids + "GetUser"));
        Assert.Single(admin.ListEntries(methodId: Ids + "GetUsers"));
        Assert.NotNull(admin.GetEntry("manual"));

        // Every type argument of a generic method, and every closed type and keyed registration of a generic interface
        await admin.InvalidateMethodAsync(keys.MethodId<IUsers>(u => u.Load<int>(0)));
        await admin.InvalidateMethodAsync(keys.MethodId<IRepository<string>>(r => r.CountAsync(0), "a"));
        Assert.Equal([keys.For<IUsers>(u => u.GetUsers(1)), "manual"], admin.ListEntries().Select(entry => entry.StoredKey).Order(StringComparer.Ordinal));

        var loads = counter.Loads;
        await users.GetUser(1);
        await users.GetUsers(1);
        Assert.Equal(loads + 1, counter.Loads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("*")]
    [InlineData("group:users")]
    [InlineData(Ids + "Find")]
    [InlineData(Ids + "Nope")]
    public async Task InvalidateMethodAsync_RejectsAnIdThatNamesNoCachedMethod(string? methodId)
    {
        await using var provider = Build();
        await provider.GetRequiredService<IUsers>().GetUser(1);
        var admin = provider.GetRequiredService<ICacheAdministrator>();

        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await admin.InvalidateMethodAsync(methodId!));
        Assert.Equal("methodId", error.ParamName);
        Assert.Single(admin.ListEntries());
    }

    [Fact]
    public async Task InvalidateMethodAsync_CanceledBeforeItStarts_RemovesNothing()
    {
        await using var provider = Build();
        await provider.GetRequiredService<IUsers>().GetUser(1);
        var admin = provider.GetRequiredService<ICacheAdministrator>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await admin.InvalidateMethodAsync(Ids + "GetUser", new CancellationToken(true)));
        Assert.Single(admin.ListEntries());
    }

    [Fact]
    public async Task QueuedInvalidations_ThrowWithTheirResult()
    {
        var time = new FakeTimeProvider();
        var backend = new ChaosDistributedCache(time: time);
        await using var provider = Build(services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.AddMethodCacheDistributedCache(_ => backend);
        });
        var users = provider.GetRequiredService<IUsers>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        await users.GetUser(1);
        await users.GetUser(2);

        backend.Inject(ChaosOperations.Set, ChaosFault.Throw() with { Times = 1 });
        var method = await Assert.ThrowsAsync<CacheInvalidationQueuedException>(async () => await admin.InvalidateMethodAsync(Ids + "GetUser"));
        Assert.Equal(new InvalidationResult { Requested = 1, Local = 1, Queued = 1 }, method.Result);
        Assert.Empty(admin.ListEntries(methodId: Ids + "GetUser"));

        var tags = await Assert.ThrowsAsync<CacheInvalidationQueuedException>(async () => await admin.InvalidateTagsAsync(["users"]));
        Assert.Equal(1, tags.Result!.Queued);
    }

    [Fact]
    public async Task HttpOnlyAdministrator_HasNoMethodEntries()
    {
        var time = new FakeTimeProvider();
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using var telemetry = new CoreTelemetry(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            MsOptions.Create(new MethodCacheTelemetryOptions()), time);
        using var store = new HttpResponseStore(MsOptions.Create(new MethodCacheMemoryOptions()), telemetry, time);
        ICacheAdministrator admin = new HttpCacheAdministrator(store);

        Assert.Empty(admin.ListEntries(methodId: Ids + "GetUser"));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await admin.InvalidateMethodAsync(Ids + "GetUser"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await admin.InvalidateMethodAsync(" "));
    }

    [Fact]
    public void QueuedException_HasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("storage down");

        Assert.False(string.IsNullOrEmpty(new CacheInvalidationQueuedException().Message));
        Assert.Null(new CacheInvalidationQueuedException().Result);
        Assert.Equal("queued", new CacheInvalidationQueuedException("queued").Message);
        var wrapped = new CacheInvalidationQueuedException("queued", inner) { Result = new InvalidationResult { Requested = 2, Queued = 1 } };
        Assert.Same(inner, wrapped.InnerException);
        Assert.Equal(1, wrapped.Result!.Queued);
        Assert.IsAssignableFrom<InvalidOperationException>(wrapped);
    }
}
