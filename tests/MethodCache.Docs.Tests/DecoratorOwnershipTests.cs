using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class DecoratorOwnershipTests
{
    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void ExistingRegistration_ContainerOwned_DisposesExactlyOnce(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        ((IServiceCollection)services).Add(new ServiceDescriptor(typeof(IOwnedSync), typeof(OwnedSync), lifetime));
        services.AddMethodCache();
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var decorated = scope.ServiceProvider.GetRequiredService<IOwnedSync>();
        var inner = OwnedSync.Last!;
        Assert.Contains("Decorator", decorated.GetType().Name);
        scope.Dispose();
        provider.Dispose();
        Assert.Equal(1, inner.Disposals);
    }

    [Fact]
    public async Task ExistingRegistration_AsyncScoped_DisposesExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddScoped<IOwnedAsync, OwnedAsync>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var decorated = scope.ServiceProvider.GetRequiredService<IOwnedAsync>();
        var inner = OwnedAsync.Last!;
        Assert.Contains("Decorator", decorated.GetType().Name);
        await scope.DisposeAsync();
        Assert.Equal(1, inner.Disposals);
    }

    [Fact]
    public void ExistingRegistration_ApplicationInstance_IsNotDisposed()
    {
        var inner = new OwnedSync();
        var services = new ServiceCollection();
        services.AddSingleton<IOwnedSync>(inner);
        services.AddMethodCache();
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOwnedSync>();
        provider.Dispose();
        Assert.Equal(0, inner.Disposals);
    }

    [Fact]
    public void GeneratedFactory_NewInstance_DisposesOwnedInstance()
    {
        var inner = new OwnedSync();
        var services = new ServiceCollection();
        services.AddIOwnedSyncWithCachingScoped(_ => inner);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope()) scope.ServiceProvider.GetRequiredService<IOwnedSync>();
        Assert.Equal(1, inner.Disposals);
    }
}

public interface IOwnedSync : IDisposable { [Cache] int Get(int id); }
public sealed class OwnedSync : IOwnedSync
{
    public static OwnedSync? Last;
    public int Disposals;
    public OwnedSync() { Last = this; }
    public int Get(int id) => id;
    public void Dispose() { Disposals++; }
}
public interface IOwnedAsync : IAsyncDisposable { [Cache] int Get(int id); }
public sealed class OwnedAsync : IOwnedAsync
{
    public static OwnedAsync? Last;
    public int Disposals;
    public OwnedAsync() { Last = this; }
    public int Get(int id) => id;
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}
