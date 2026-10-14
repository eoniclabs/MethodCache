using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// Keyed registrations are cached like unkeyed ones, and an interface registered only keyed stays keyed-only.
/// </summary>
public class KeyedRegistrationTests
{
    [Fact]
    public void KeyedRegistration_IsDecorated_AndNoUnkeyedServiceAppears()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates, KeyedRates>("ecb");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.Contains("Decorator", provider.GetRequiredKeyedService<IKeyedRates>("ecb").GetType().Name);
        Assert.Null(provider.GetService<IKeyedRates>());
    }

    // The service key is part of the cache key, so each registration caches its own results
    [Fact]
    public void KeyedRegistrations_AreEachDecorated_AndFactoriesSeeTheirOwnKey()
    {
        var ecb = new KeyedRates(1);
        var fed = new KeyedRates(2);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates>("ecb", ecb);
        services.AddKeyedScoped<IKeyedRates>("fed", (_, key) => key is "fed" ? fed : throw new InvalidOperationException($"Factory saw key {key}"));
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var first = scope.ServiceProvider.GetRequiredKeyedService<IKeyedRates>("ecb");
        Assert.Equal(1, first.Get(1));
        Assert.Equal(1, first.Get(1));
        Assert.Equal(1, ecb.Calls);

        var second = scope.ServiceProvider.GetRequiredKeyedService<IKeyedRates>("fed");
        Assert.Contains("Decorator", second.GetType().Name);
        Assert.Equal(2, second.Get(1));
        Assert.Equal(2, second.Get(1));
        Assert.Equal(1, fed.Calls);
    }

    [Fact]
    public void KeyedScopedRegistrations_WithTheSameArguments_EachReturnTheirOwnValue()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IKeyedRates>("ecb", (_, _) => new KeyedRates(1));
        services.AddKeyedScoped<IKeyedRates>("fed", (_, _) => new KeyedRates(2));
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            Assert.Equal(1, scope.ServiceProvider.GetRequiredKeyedService<IKeyedRates>("ecb").Get(1));
            Assert.Equal(2, scope.ServiceProvider.GetRequiredKeyedService<IKeyedRates>("fed").Get(1));
        }

        using (var scope = provider.CreateScope())
        {
            Assert.Equal(1, scope.ServiceProvider.GetRequiredKeyedService<IKeyedRates>("ecb").Get(1));
            Assert.Equal(2, scope.ServiceProvider.GetRequiredKeyedService<IKeyedRates>("fed").Get(1));
        }
    }

    [Fact]
    public void KeyedEntries_HaveTheServiceKeyInTheirCacheKey()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates>("ecb", new KeyedRates(1));
        services.AddKeyedSingleton<IKeyedRates>(KeyedRegion.Eu, new KeyedRates(2));
        services.AddKeyedSingleton<IKeyedRates>(7, new KeyedRates(3));
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheAdministrator>();

        provider.GetRequiredKeyedService<IKeyedRates>("ecb").Get(5);
        provider.GetRequiredKeyedService<IKeyedRates>(KeyedRegion.Eu).Get(5);
        provider.GetRequiredKeyedService<IKeyedRates>(7).Get(5);

        foreach (var key in new object[] { "ecb", KeyedRegion.Eu, 7 })
        {
            var expected = MethodCacheKeys.For(typeof(IKeyedRates), MethodCacheKeys.KeyedMethodName(nameof(IKeyedRates.Get), key), MethodCacheKeys.Part(5));
            Assert.NotNull(cache.GetEntry(expected));
        }
    }

    // A cacheable AnyKey registration must fail instead of silently bypassing its generated binding.
    [Fact]
    public void AnyKeyRegistration_ReportsUnsupportedBinding()
    {
        var rates = new KeyedRates(1);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates>(KeyedService.AnyKey, rates);
        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCache());
        Assert.Contains("AnyKey", error.Message);
        Assert.Contains(nameof(IKeyedRates), error.Message);
        Assert.Equal(0, rates.Calls);
    }

    // A null service key makes an unkeyed registration, cached like one
    [Fact]
    public void NullServiceKey_IsAnUnkeyedRegistration()
    {
        var rates = new KeyedRates(1);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates>(null, rates);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IKeyedRates>();
        service.Get(1);
        service.Get(1);
        Assert.Equal(1, rates.Calls);
    }

    [Fact]
    public void KeyedRegistration_WithAKeyThatHasNoStableStringForm_IsRejected()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates>(new object(), new KeyedRates(1));

        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCache());
        Assert.Contains(nameof(IKeyedRates), error.Message);
        Assert.Contains("service key", error.Message);
    }

    // Review G7: a keyed registration next to an unkeyed one of the same interface is cached too
    [Fact]
    public void CachedCall_KeyedRegistrationNextToAnUnkeyedOne_IsCached()
    {
        var keyed = new KeyedRates(1);
        var unkeyed = new KeyedRates(2);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates>("k", keyed);
        services.AddSingleton<IKeyedRates>(unkeyed);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(1, provider.GetRequiredKeyedService<IKeyedRates>("k").Get(10));
        Assert.Equal(1, provider.GetRequiredKeyedService<IKeyedRates>("k").Get(10));
        Assert.Equal(2, provider.GetRequiredService<IKeyedRates>().Get(10));
        Assert.Equal(2, provider.GetRequiredService<IKeyedRates>().Get(10));

        Assert.Equal(1, keyed.Calls);
        Assert.Equal(1, unkeyed.Calls);
    }

    [Fact]
    public void AddMethodCacheTwice_KeyedRegistration_IsDecoratedOnce()
    {
        var inner = new KeyedRates(1);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedRates>("ecb", inner);
        services.AddMethodCache();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        var rates = provider.GetRequiredKeyedService<IKeyedRates>("ecb");
        rates.Get(1);
        rates.Get(1);

        Assert.Equal(1, inner.Calls);
        Assert.Single(services, d => d.ServiceType == typeof(IKeyedRates) && Equals(d.ServiceKey, "ecb"));
    }
}

public interface IKeyedRates { [Cache] int Get(int id); }

public enum KeyedRegion { Eu, Us }

public sealed class KeyedRates : IKeyedRates
{
    private readonly int _value;

    public KeyedRates() : this(0)
    {
    }

    public KeyedRates(int value) => _value = value;

    public int Calls { get; private set; }

    public int Get(int id)
    {
        Calls++;
        return _value;
    }
}
