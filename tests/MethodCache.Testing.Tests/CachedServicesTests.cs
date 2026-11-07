using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Testing.Tests;

public class CachedServicesTests
{
    public interface IPriceSource
    {
        decimal Price(int id);
    }

    // Needs an IPriceSource, which a test container may lack
    public sealed class SourcedProducts(IPriceSource source) : IProducts
    {
        public Task<string> GetAsync(int id) => Task.FromResult($"product {id}");
        public Task<decimal> PriceAsync(int id) => Task.FromResult(source.Price(id));
    }

    [Fact]
    public void Problems_EveryServiceResolvesToItsDecorator_IsEmpty()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddScoped<IProductWriter, ProductWriter>();
        services.AddKeyedTransient<IImportedPrices, ImportedPrices>("import");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.Empty(CachedServices.Problems(provider));
    }

    [Fact]
    public void Problems_AServiceRegisteredAfterAddMethodCache_IsReported()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProductWriter, ProductWriter>();
        services.AddMethodCache();
        services.AddSingleton<IProducts, Products>();
        using var provider = services.BuildServiceProvider();

        var problem = Assert.Single(CachedServices.Problems(provider));

        Assert.Equal(typeof(IProducts), problem.Service);
        Assert.Null(problem.ServiceKey);
        Assert.Contains("was registered after AddMethodCache and has no generated decorator", problem.Problem, StringComparison.Ordinal);
        Assert.StartsWith($"{typeof(IProducts)}: Cacheable service", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_AServiceThatCantBeResolved_IsReportedWithTheError()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IProducts, SourcedProducts>("sourced");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        var problem = Assert.Single(CachedServices.Problems(provider));

        Assert.Equal("sourced", problem.ServiceKey);
        Assert.StartsWith("Resolving it throws InvalidOperationException:", problem.Problem, StringComparison.Ordinal);
        Assert.Contains(nameof(IPriceSource), problem.Problem, StringComparison.Ordinal);
        Assert.StartsWith($"{typeof(IProducts)} (key sourced): Resolving it throws", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_WithoutAddMethodCache_Throws()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => CachedServices.Problems(provider));

        Assert.Contains("AddMethodCache", error.Message, StringComparison.Ordinal);
    }
}
