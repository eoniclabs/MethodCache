using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// A keyed implementation that takes its service key in its constructor gets the application's key, not the
/// private key MethodCache moves the registration to, and the container still owns the instance.
/// </summary>
public class ServiceKeyInjectionTests
{
    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task Resolve_ServiceKeyParameter_GetsTheApplicationsKey(ServiceLifetime lifetime)
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(ServiceDescriptor.DescribeKeyed(typeof(IRegion), "eu", typeof(StringKeyRegion), lifetime));
        services.Add(ServiceDescriptor.DescribeKeyed(typeof(IRegion), 7, typeof(ObjectKeyRegion), lifetime));
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        Assert.Equal("eu:1", scope.ServiceProvider.GetRequiredKeyedService<IRegion>("eu").Name(1));
        Assert.Equal("7:1", scope.ServiceProvider.GetRequiredKeyedService<IRegion>(7).Name(1));
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton, false)]
    [InlineData(ServiceLifetime.Scoped, false)]
    [InlineData(ServiceLifetime.Transient, false)]
    [InlineData(ServiceLifetime.Singleton, true)]
    [InlineData(ServiceLifetime.Scoped, true)]
    [InlineData(ServiceLifetime.Transient, true)]
    public async Task Dispose_ServiceKeyParameter_ContainerDisposesTheImplementation(ServiceLifetime lifetime, bool asyncOnly)
    {
        var disposed = new List<string>();
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(disposed);
        services.Add(ServiceDescriptor.DescribeKeyed(typeof(IRegion), "eu", asyncOnly ? typeof(AsyncDisposableRegion) : typeof(DisposableRegion), lifetime));
        services.AddMethodCache();
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal("eu:1", scope.ServiceProvider.GetRequiredKeyedService<IRegion>("eu").Name(1));
        }

        Assert.Equal(lifetime == ServiceLifetime.Singleton ? new List<string>() : ["eu"], disposed);
        await provider.DisposeAsync();
        Assert.Equal(["eu"], disposed);
    }

    [Fact]
    public void Resolve_ServiceKeyOfTheWrongType_FailsAsWithoutMethodCache()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddKeyedSingleton<IRegion, StringKeyRegion>(7);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<IRegion>(7));

        Assert.Contains("ServiceKey", error.Message);
    }

    [Fact]
    public void Resolve_FromKeyedServicesInheritingTheKey_ResolvesTheDependencyWithTheApplicationsKey()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddKeyedSingleton("eu", new Currency("EUR"));
        services.AddKeyedSingleton("us", new Currency("USD"));
        services.AddSingleton(new Currency("none"));
        services.AddKeyedScoped<IRegion, InheritingRegion>("eu");
        services.AddKeyedScoped<IRegion, ExplicitRegion>("us");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal("EUR:1", scope.ServiceProvider.GetRequiredKeyedService<IRegion>("eu").Name(1));
        Assert.Equal("USD+none:1", scope.ServiceProvider.GetRequiredKeyedService<IRegion>("us").Name(1));
    }

    [Fact]
    public void Resolve_AmbiguousConstructors_FailsAsWithoutMethodCache()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(new Currency("EUR"));
        services.AddSingleton(new List<string>());
        services.AddKeyedSingleton<IRegion, AmbiguousRegion>("eu");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<IRegion>("eu"));

        Assert.Contains("ambiguous", error.Message);
    }

    [Fact]
    public void Resolve_NoUsableConstructor_FailsAsWithoutMethodCache()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddKeyedSingleton<IRegion, AmbiguousRegion>("eu");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<IRegion>("eu"));
    }

    [Fact]
    public void Resolve_DefaultValues_AreUsedForMissingServices()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddKeyedSingleton<IRegion, DefaultsRegion>("eu");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.Equal("eu/0/none/0:1", provider.GetRequiredKeyedService<IRegion>("eu").Name(1));
    }

    public interface IRegion
    {
        [Cache] string Name(int id);
    }

    public sealed record Currency(string Code);

    public sealed class StringKeyRegion([ServiceKey] string key) : IRegion
    {
        public string Name(int id) => $"{key}:{id}";
    }

    public sealed class ObjectKeyRegion([ServiceKey] object key) : IRegion
    {
        public string Name(int id) => $"{key}:{id}";
    }

    public sealed class DisposableRegion([ServiceKey] string key, List<string> disposed) : IRegion, IDisposable
    {
        public string Name(int id) => $"{key}:{id}";
        public void Dispose() => disposed.Add(key);
    }

    public sealed class AsyncDisposableRegion([ServiceKey] string key, List<string> disposed) : IRegion, IAsyncDisposable
    {
        public string Name(int id) => $"{key}:{id}";
        public ValueTask DisposeAsync()
        {
            disposed.Add(key);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class InheritingRegion([FromKeyedServices] Currency currency) : IRegion
    {
        public string Name(int id) => $"{currency.Code}:{id}";
    }

    public sealed class ExplicitRegion([FromKeyedServices("us")] Currency currency, [FromKeyedServices(null)] Currency unkeyed, [ServiceKey] string key) : IRegion
    {
        public string Name(int id) => key == "us" ? $"{currency.Code}+{unkeyed.Code}:{id}" : "wrong key";
    }

    public sealed class AmbiguousRegion : IRegion
    {
        public AmbiguousRegion([ServiceKey] string key, Currency currency) => Key = key + currency.Code;
        public AmbiguousRegion([ServiceKey] string key, List<string> list) => Key = key + list.Count;
        public string Key { get; }
        public string Name(int id) => $"{Key}:{id}";
    }

    public sealed class DefaultsRegion([ServiceKey] string key, int number = 0, string text = "none", CancellationToken token = default) : IRegion
    {
        public string Name(int id) => $"{key}/{number}/{text}/{(token.CanBeCanceled ? 1 : 0)}:{id}";
    }
}
