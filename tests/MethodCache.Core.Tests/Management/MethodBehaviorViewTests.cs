using MethodCache.Core.Runtime.Distributed;
using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Management;

/// <summary>
/// What <see cref="ICacheAdministrator.GetMethods"/> says a method does on this instance: how long its entries live with
/// defaults filled in, where they are kept and why, which parameters make up its keys, and which results weren't shared.
/// </summary>
public class MethodBehaviorViewTests
{
    private const string Ids = "[MethodCache.Core.Tests]MethodCache.Core.Tests.Management.MethodBehaviorViewTests.IViewed.";

    // The serializer can't set Value, so the value doesn't read back unchanged
    public sealed class Hidden
    {
        public int Value { get; private set; }
        public static Hidden Of(int value) => new() { Value = value };
    }

    public interface IViewed
    {
        [Cache] Task<int> DefaultAsync(int id, CancellationToken cancellationToken);
        [Cache(Duration = "1h", SlidingExpiration = "5m")] Task<int> SlidingAsync(int id, string region);
        [Cache(SlidingExpiration = "30m")] Task<int> SlidingOnlyAsync(int id);
        [Cache(Duration = "3d")] Task<int> LongAsync(int id);
        [Cache(Duration = "10m", Distributed = false)] Task<int> LocalAsync(int id);
        [Cache(Duration = "10m")] int Read(int id);
        [Cache(Duration = "10m")] Task<Hidden> HiddenAsync(int id);
    }

    public sealed class Viewed : IViewed
    {
        public Task<int> DefaultAsync(int id, CancellationToken cancellationToken) => Task.FromResult(id);
        public Task<int> SlidingAsync(int id, string region) => Task.FromResult(id);
        public Task<int> SlidingOnlyAsync(int id) => Task.FromResult(id);
        public Task<int> LongAsync(int id) => Task.FromResult(id);
        public Task<int> LocalAsync(int id) => Task.FromResult(id);
        public int Read(int id) => id;
        public Task<Hidden> HiddenAsync(int id) => Task.FromResult(Hidden.Of(id));
    }

    public interface IConfigured
    {
        [Cache] Task<int> FromResultAsync(int id);
        [Cache(Duration = "1h")] Task<int> SlidesFromResultAsync(int id);
        [Cache] Task<int> CodeLocalAsync(int id);
        [Cache(Enabled = false)] Task<int> OffAsync(int id);
        [Cache] Task<int> UnsetAsync(int id);
        [Cache(Distributed = true)] Task<int> SharedAsync(int id);
    }

    public sealed class Configured : IConfigured
    {
        public Task<int> FromResultAsync(int id) => Task.FromResult(id);
        public Task<int> SlidesFromResultAsync(int id) => Task.FromResult(id);
        public Task<int> CodeLocalAsync(int id) => Task.FromResult(id);
        public Task<int> OffAsync(int id) => Task.FromResult(id);
        public Task<int> UnsetAsync(int id) => Task.FromResult(id);
        public Task<int> SharedAsync(int id) => Task.FromResult(id);
    }

    private static ServiceProvider Build(bool distributed)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IViewed, Viewed>();
        services.AddMethodCache();
        if (distributed) services.AddMethodCacheDistributedCache(_ => new AtomicTestCache(time));
        return services.BuildServiceProvider();
    }

    private static CachedMethodInfo Method(ServiceProvider provider, string name)
        => provider.GetRequiredService<ICacheAdministrator>().GetMethods().Single(method => method.MethodId == Ids + name);

    [Fact]
    public void WithoutADistributedCache_ValuesStayInMemory_AndTheDefaultDurationIsFilledIn()
    {
        using var provider = Build(distributed: false);
        var method = Method(provider, "DefaultAsync");

        Assert.Equal(TimeSpan.FromMinutes(5), method.Lifetime.Duration);
        Assert.Equal(TimeSpan.FromDays(7), method.Lifetime.MaxExpiration);
        Assert.Null(method.Lifetime.Distributed);
        Assert.False(method.Storage.Distributed);
        Assert.Null(method.Storage.Store);
        Assert.Equal("No distributed cache is registered.", method.Storage.MemoryOnlyReason);
        Assert.Equal(["id"], method.KeyParameters);
    }

    [Fact]
    public void WithADistributedCache_TheStoredCopyLivesOneWindow_AtMostTheDuration_AndAtMostTheTagRetention()
    {
        using var provider = Build(distributed: true);
        var maxStored = provider.GetRequiredService<DistributedCacheLayer>().MaxStoredLifetime;

        var sliding = Method(provider, "SlidingAsync");
        Assert.Equal(TimeSpan.FromHours(1), sliding.Lifetime.Duration);
        Assert.Equal(TimeSpan.FromMinutes(5), sliding.Lifetime.SlidingExpiration);
        Assert.Equal(TimeSpan.FromMinutes(5), sliding.Lifetime.Distributed);
        Assert.True(sliding.Storage.Distributed);
        Assert.Equal("AtomicTestCache", sliding.Storage.Store);
        Assert.Null(sliding.Storage.MemoryOnlyReason);
        Assert.Equal(["id", "region"], sliding.KeyParameters);

        var slidingOnly = Method(provider, "SlidingOnlyAsync");
        Assert.Equal(TimeSpan.FromDays(7), slidingOnly.Lifetime.Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), slidingOnly.Lifetime.Distributed);

        Assert.Equal(TimeSpan.FromMinutes(5), Method(provider, "DefaultAsync").Lifetime.Distributed);
        Assert.Equal(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(2), maxStored);
        Assert.Equal(maxStored, Method(provider, "LongAsync").Lifetime.Distributed);
    }

    [Fact]
    public void AMethodKeptInMemory_SaysWhy()
    {
        using var provider = Build(distributed: true);

        var local = Method(provider, "LocalAsync");
        Assert.False(local.Storage.Distributed);
        Assert.Equal("Distributed is false in the method's attribute.", local.Storage.MemoryOnlyReason);
        Assert.Null(local.Lifetime.Distributed);

        Assert.Equal("A synchronous method keeps its values in memory.", Method(provider, "Read").Storage.MemoryOnlyReason);
    }

    [Fact]
    public async Task AResultTypeThatDoesntReadBackUnchanged_IsListedWithItsReasonAndCount()
    {
        using var provider = Build(distributed: true);
        var viewed = provider.GetRequiredService<IViewed>();
        Assert.Empty(Method(provider, "HiddenAsync").NotShared);

        await viewed.HiddenAsync(1);
        await viewed.HiddenAsync(2);

        var notShared = Assert.Single(Method(provider, "HiddenAsync").NotShared);
        Assert.Equal(typeof(Hidden).FullName, notShared.Type);
        Assert.Equal(typeof(Hidden).FullName, notShared.DeclaredType);
        Assert.Equal("These values don't read back unchanged from the serializer.", notShared.Reason);
        Assert.Equal(2, notShared.Count);
        Assert.Empty(Method(provider, "DefaultAsync").NotShared);
    }

    [Fact]
    public void ATimeFromTheResult_AndSettingsFromCodeAndStoreValuesByDefault_AreShownWithTheirSource()
    {
        const string configured = "[MethodCache.Core.Tests]MethodCache.Core.Tests.Management.MethodBehaviorViewTests.IConfigured.";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IConfigured, Configured>();
        services.AddMethodCache(mc =>
        {
            mc.Method((IConfigured c) => c.FromResultAsync(Any<int>.Value)).DurationFor(seconds => TimeSpan.FromSeconds(seconds));
            mc.Method((IConfigured c) => c.SlidesFromResultAsync(Any<int>.Value)).SlidingFor(seconds => TimeSpan.FromSeconds(seconds));
            mc.Method((IConfigured c) => c.CodeLocalAsync(Any<int>.Value)).Configure(o => o.WithDistributed(false));
        });
        services.AddMethodCacheDistributedCache(_ => new AtomicTestCache(time), o => o.StoreValuesByDefault = false);
        using var provider = services.BuildServiceProvider();
        CachedMethodInfo Get(string name) => provider.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == configured + name);

        var fromResult = Get("FromResultAsync");
        Assert.True(fromResult.Lifetime.DurationFromResult);
        Assert.Null(fromResult.Lifetime.Duration);
        Assert.Null(fromResult.Lifetime.Distributed);

        var slides = Get("SlidesFromResultAsync");
        Assert.True(slides.Lifetime.SlidingFromResult);
        Assert.Null(slides.Lifetime.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(1), slides.Lifetime.Duration);

        Assert.Equal("Distributed is false in startup code.", Get("CodeLocalAsync").Storage.MemoryOnlyReason);
        Assert.Equal("Caching is turned off for the method (Enabled is false).", Get("OffAsync").Storage.MemoryOnlyReason);
        Assert.Equal("StoreValuesByDefault is false and the method doesn't set Distributed.", Get("UnsetAsync").Storage.MemoryOnlyReason);
        var shared = Get("SharedAsync");
        Assert.True(shared.Storage.Distributed);
        Assert.Equal(TimeSpan.FromMinutes(5), shared.Lifetime.Distributed);
    }
}
