using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// An interface registered more than once under the same service key: each registration caches its own results,
/// with its implementation type in its cache keys, while a single registration keeps its plain keys.
/// </summary>
public class MultipleRegistrationTests
{
    private const string Prefix = "[MethodCache.Core.Tests]MethodCache.Core.Tests.Core.MultipleRegistrationTests+IRates.Get";
    private const string TypeA = "#[MethodCache.Core.Tests]MethodCache.Core.Tests.Core.MultipleRegistrationTests+RatesA";
    private const string TypeB = "#[MethodCache.Core.Tests]MethodCache.Core.Tests.Core.MultipleRegistrationTests+RatesB";

    [Fact]
    public void GetServices_TwoImplementations_EachReturnsAndCachesItsOwnResults()
    {
        var a = new RatesA();
        var b = new RatesB();
        using var provider = Build(services => services.AddSingleton<IRates>(a).AddSingleton<IRates>(b));

        var all = provider.GetServices<IRates>().ToArray();

        Assert.Equal(["A1", "B1"], all.Select(rates => rates.Get(1)));
        Assert.Equal(["A1", "B1"], all.Select(rates => rates.Get(1)));
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
    }

    [Fact]
    public void Keys_SingleRegistration_StayPlain_SeveralCarryTheImplementationType()
    {
        using var single = Build(services => services.AddSingleton<IRates, RatesA>());
        single.GetRequiredService<IRates>().Get(1);
        Assert.Equal([Prefix + ":1"], StoredKeys(single));
        Assert.Equal(Prefix + ":1", single.GetRequiredService<IMethodCacheKeys>().For<IRates>(rates => rates.Get(1)));

        using var several = Build(services => services.AddSingleton<IRates, RatesA>().AddSingleton<IRates, RatesB>()
            .AddKeyedSingleton<IRates, RatesA>("eu").AddKeyedSingleton<IRates, RatesB>("eu").AddKeyedSingleton<IRates, RatesA>("us"));
        foreach (var rates in several.GetServices<IRates>()
                     .Concat(several.GetKeyedServices<IRates>("eu"))
                     .Concat(several.GetKeyedServices<IRates>("us")))
            rates.Get(1);

        Assert.Equal(
            new[] { Prefix + TypeA + ":1", Prefix + TypeA + "@eu:1", Prefix + TypeB + ":1", Prefix + TypeB + "@eu:1", Prefix + "@us:1" }.Order(StringComparer.Ordinal),
            StoredKeys(several));
    }

    [Fact]
    public void For_SeveralRegistrations_ReturnsTheKeyOfTheOneGetServiceReturns()
    {
        using var provider = Build(services => services.AddSingleton<IRates, RatesA>().AddSingleton<IRates, RatesB>()
            .AddKeyedSingleton<IRates, RatesB>("eu").AddKeyedSingleton<IRates, RatesA>("eu"));
        var keys = provider.GetRequiredService<IMethodCacheKeys>();

        Assert.Equal(Prefix + TypeB + ":1", keys.For<IRates>(rates => rates.Get(1)));
        Assert.Equal(Prefix + TypeA + "@eu:1", keys.For<IRates>(rates => rates.Get(1), "eu"));
    }

    [Fact]
    public async Task For_SeveralRegistrations_InvalidatesOnlyTheResolvedOnesEntry()
    {
        var a = new RatesA();
        var b = new RatesB();
        await using var provider = Build(services => services.AddSingleton<IRates>(a).AddSingleton<IRates>(b));
        var all = provider.GetServices<IRates>().ToArray();
        foreach (var rates in all) rates.Get(1);

        await provider.GetRequiredService<ICacheManager>().InvalidateKeyAsync(
            provider.GetRequiredService<IMethodCacheKeys>().For<IRates>(rates => rates.Get(1)));
        foreach (var rates in all) rates.Get(1);

        Assert.Equal(1, a.Calls);
        Assert.Equal(2, b.Calls);
    }

    [Fact]
    public void For_SeveralFactoryRegistrations_UsesTheTypeTheLastFactoryCreates()
    {
        using var provider = Build(services => services
            .AddScoped<IRates>(_ => new RatesA())
            .AddScoped<IRates>(_ => new RatesB()));

        Assert.Equal(Prefix + TypeB + ":1", provider.GetRequiredService<IMethodCacheKeys>().For<IRates>(rates => rates.Get(1)));
    }

    [Fact]
    public async Task For_ScopedFactoryWithSlowAsyncDisposal_ReturnsWithoutWaitingForTheDisposal()
    {
        var disposal = new TaskCompletionSource();
        var disposed = new TaskCompletionSource();
        using var provider = Build(services => services
            .AddScoped<IRates>(_ => new RatesA())
            .AddScoped<IRates>(_ => new SlowDisposingRates(disposal.Task, disposed)));

        var key = provider.GetRequiredService<IMethodCacheKeys>().For<IRates>(rates => rates.Get(1));

        Assert.Equal(Prefix + "#[MethodCache.Core.Tests]MethodCache.Core.Tests.Core.MultipleRegistrationTests+SlowDisposingRates:1", key);
        Assert.False(disposed.Task.IsCompleted);
        disposal.SetResult();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TagInvalidation_SeveralRegistrations_ReachesEveryOne()
    {
        var a = new RatesA();
        var b = new RatesB();
        await using var provider = Build(services => services.AddSingleton<IRates>(a).AddSingleton<IRates>(b)
            .AddKeyedSingleton<IRates, RatesA>("eu"));
        var all = provider.GetServices<IRates>().Append(provider.GetRequiredKeyedService<IRates>("eu")).ToArray();
        foreach (var rates in all) rates.Tagged(1);

        // [CacheInvalidate] on one registration, then the administrator
        await all[0].ResetAsync();
        foreach (var rates in all) rates.Tagged(1);
        Assert.Equal(2, a.TaggedCalls);
        Assert.Equal(2, b.TaggedCalls);

        await provider.GetRequiredService<ICacheAdministrator>().InvalidateTagsAsync(["rates"]);
        foreach (var rates in all) rates.Tagged(1);
        Assert.Equal(3, a.TaggedCalls);
        Assert.Equal(3, b.TaggedCalls);
        Assert.Equal(3, provider.GetRequiredService<ICacheAdministrator>().ListEntries(tag: "rates").Count);
    }

    [Fact]
    public void GetServices_TwoTypeRegistrationsOfTheSameType_ShareCachedResults()
    {
        // The container builds both from the same type and dependencies, so they are interchangeable: a library and the
        // application both adding the same registration must keep working
        using var provider = Build(services => services.AddSingleton<IRates, RatesA>().AddScoped<IRates, RatesA>());
        using var scope = provider.CreateScope();

        var all = scope.ServiceProvider.GetServices<IRates>().ToArray();

        Assert.Equal(["A1", "A1"], all.Select(rates => rates.Get(1)));
        Assert.Equal([Prefix + TypeA + ":1"], StoredKeys(provider));
    }

    [Fact]
    public void Resolve_TypeRegistrationAndFactoryCreatingTheSameType_Fails()
    {
        // A factory may configure its instance differently, so it must not share the type registration's results
        using var provider = Build(services => services.AddSingleton<IRates, RatesA>().AddSingleton<IRates>(_ => new RatesA()));

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IRates>().ToArray());

        Assert.Contains(typeof(RatesA).FullName!, error.Message);
    }

    [Fact]
    public void AddMethodCache_TypeRegistrationAndInstanceOfTheSameType_IsRejected()
    {
        var services = new ServiceCollection().AddSingleton<IRates, RatesA>().AddSingleton<IRates>(new RatesA());

        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCache());

        Assert.Contains(typeof(RatesA).FullName!, error.Message);
        Assert.Contains("service keys", error.Message);
    }

    [Fact]
    public void AddMethodCache_TwoInstancesOfTheSameTypeUnderOneKey_IsRejected()
    {
        var services = new ServiceCollection().AddKeyedSingleton<IRates>("eu", new RatesA()).AddKeyedSingleton<IRates>("eu", new RatesA());

        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCache());

        Assert.Contains("service key eu", error.Message);
    }

    [Fact]
    public void Resolve_TwoFactoriesCreatingTheSameType_FailsOnlyWhenBothAreCreated()
    {
        using var provider = Build(services => services.AddSingleton<IRates>(_ => new RatesA()).AddSingleton<IRates>(_ => new RatesA()));

        Assert.Equal("A1", provider.GetRequiredService<IRates>().Get(1));
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IRates>().ToArray());
        Assert.Contains(typeof(RatesA).FullName!, error.Message);
        Assert.Contains("service keys", error.Message);
    }

    [Fact]
    public void Resolve_FakeFactoryOfTheRealType_WorksWhenOnlyTheFakeIsResolved()
    {
        var fake = new RatesA();
        using var provider = Build(services => services.AddSingleton<IRates, RatesA>().AddSingleton<IRates>(_ => fake));

        var rates = provider.GetRequiredService<IRates>();
        rates.Get(1);
        rates.Get(1);

        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void AddMethodCacheTwice_RegistrationAddedBetween_BothCacheTheirOwnResults()
    {
        var a = new RatesA();
        var b = new RatesB();
        var services = new ServiceCollection().AddSingleton<IRates>(a);
        services.AddMethodCache();
        services.AddSingleton<IRates>(b);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(["A1", "B1"], provider.GetServices<IRates>().Select(rates => rates.Get(1)));
        Assert.Equal([Prefix + TypeA + ":1", Prefix + TypeB + ":1"], StoredKeys(provider));
    }

    [Fact]
    public void AddMethodCacheTwice_RegistrationRemovedBetween_KeepsPlainKeys()
    {
        var services = new ServiceCollection().AddSingleton<IRates, RatesA>();
        services.AddMethodCache();
        services.RemoveAll<IRates>();
        services.AddSingleton<IRates, RatesA>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRates>().Get(1);

        Assert.Equal([Prefix + ":1"], StoredKeys(provider));
    }

    [Fact]
    public void Resolve_RegistrationAddedAfterAddMethodCache_StillReportsTheOrderError()
    {
        var services = new ServiceCollection().AddSingleton<IRates, RatesA>();
        services.AddMethodCache();
        services.AddSingleton<IRates, RatesB>();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IRates>().ToArray());

        Assert.Contains("registered after AddMethodCache", error.Message);
    }

    [Fact]
    public async Task DistributedLookup_KeyWithAnImplementationPart_FindsTheMethodsPolicy()
    {
        await using var provider = Build(services => services.AddSingleton<IRates, RatesA>().AddKeyedSingleton<IRates, RatesB>("eu")
            .AddKeyedSingleton<IRates, RatesA>("eu"));
        var parameters = provider.GetRequiredService<MethodCacheServiceCollectionExtensions.GeneratedParameters>();
        var lookup = parameters.CreateLookup(() => provider.GetRequiredService<MethodCache.Policies.IPolicyRegistry>());

        var methodId = lookup(Prefix + ":1")!.MethodId;
        Assert.Equal(methodId, lookup(Prefix + TypeA + "@eu:1")?.MethodId);
        Assert.Equal(methodId, lookup(Prefix + "#[Odd\\:Assembly]Odd\\@Type@eu:1")?.MethodId);
        Assert.Null(lookup(Prefix + TypeA + "@us:1"));
    }

    [Theory]
    [InlineData(typeof(RatesA), "#[MethodCache.Core.Tests]MethodCache.Core.Tests.Core.MultipleRegistrationTests+RatesA")]
    [InlineData(typeof(Dictionary<string, int>), "#[System.Private.CoreLib]System.Collections.Generic.Dictionary`2[[System.Private.CoreLib]System.String,[System.Private.CoreLib]System.Int32]")]
    public void ImplementationPart_IsTheStableTypeName(Type type, string expected)
        => Assert.Equal(expected, MethodCacheServiceCollectionExtensions.ImplementationPart(type));

    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        services.AddMethodCache();
        return services.BuildServiceProvider();
    }

    private static List<string> StoredKeys(IServiceProvider provider)
        => provider.GetRequiredService<ICacheAdministrator>().ListEntries().Select(entry => entry.StoredKey).Order(StringComparer.Ordinal).ToList();

    public interface IRates
    {
        [Cache] string Get(int id);

        [Cache(Tags = new[] { "rates" })] string Tagged(int id);

        [CacheInvalidate(Tags = new[] { "rates" })] Task ResetAsync();
    }

    public abstract class RatesBase(string name) : IRates
    {
        public int Calls { get; private set; }
        public int TaggedCalls { get; private set; }
        public string Get(int id)
        {
            Calls++;
            return name + id;
        }

        public string Tagged(int id)
        {
            TaggedCalls++;
            return name + id;
        }

        public Task ResetAsync() => Task.CompletedTask;
    }

    public sealed class RatesA() : RatesBase("A");

    public sealed class RatesB() : RatesBase("B");

    public sealed class SlowDisposingRates(Task disposal, TaskCompletionSource disposed) : RatesBase("S"), IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await disposal;
            disposed.SetResult();
        }
    }
}
