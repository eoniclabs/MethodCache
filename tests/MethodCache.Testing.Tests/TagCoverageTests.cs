using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Testing.Tests;

public class TagCoverageTests
{
    [Fact]
    public void Uninvalidated_ListsTheTagsNothingInvalidates()
    {
        using var provider = Build();

        var uninvalidated = TagCoverage.Uninvalidated(provider);

        var tag = Assert.Single(uninvalidated);
        Assert.Equal("prices", tag.Tag);
        Assert.EndsWith("IProducts.PriceAsync", tag.MethodId, StringComparison.Ordinal);
        Assert.Equal($"prices on {tag.MethodId}", tag.ToString());
    }

    [Theory]
    [InlineData("prices")]
    [InlineData("pri{x}")]
    public void Uninvalidated_AlsoInvalidatedBy_CoversATag(string invalidated)
    {
        using var provider = Build();

        Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: [invalidated]));
    }

    [Fact]
    public void Uninvalidated_InvalidatedElsewhere_CountsOnlyWhenAReferencedProjectInvalidatesTheTag()
    {
        using var provider = BuildWithImportedPrices();

        // The price import invalidates imported-prices and imported-price:{id}, and nothing invalidates currency-rates
        var tag = Assert.Single(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: ["prices"]));

        Assert.Equal("currency-rates", tag.Tag);
        Assert.True(tag.MarkedInvalidatedElsewhere);
        Assert.EndsWith("(marked InvalidatedElsewhere, but no project the tests reference invalidates it)", tag.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninvalidated_InvalidatedElsewhere_AlsoInvalidatedBy_CoversATagNoProjectInvalidates()
    {
        using var provider = BuildWithImportedPrices();

        Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: ["prices", "currency-rates"]));
    }

    [Fact]
    public void Uninvalidated_InvalidationsInTheTestProject_DontCount()
    {
        // CacheRecorderTests invalidates "prices" itself, which says nothing about the application
        using var provider = Build();

        Assert.False(Assert.Single(TagCoverage.Uninvalidated(provider)).MarkedInvalidatedElsewhere);
        Assert.DoesNotContain(ReferencedInvalidations.Read(AppContext.BaseDirectory), tag => tag == "prices");
        Assert.Contains("imported-price:{}", ReferencedInvalidations.Read(AppContext.BaseDirectory));
    }

    private static ServiceProvider BuildWithImportedPrices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddSingleton<IProductWriter, ProductWriter>();
        services.AddSingleton<IImportedPrices, ImportedPrices>();
        services.AddMethodCache();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Uninvalidated_ConfiguredTag_IsChecked()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Methods:[MethodCache.Testing.Tests]MethodCache.Testing.Tests.IProducts.GetAsync:Tags:0"] = "catalog"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddSingleton<IProductWriter, ProductWriter>();
        services.AddMethodCache(mc => mc.FromConfiguration(configuration));
        using var provider = services.BuildServiceProvider();

        var uninvalidated = TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: ["prices"]);

        Assert.Equal("catalog", Assert.Single(uninvalidated).Tag);
    }

    [Fact]
    public void Uninvalidated_WithoutTheInvalidatingService_ReportsItsTags()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        var tags = TagCoverage.Uninvalidated(provider).Select(static t => t.Tag).ToArray();

        Assert.Equal(["product:{id}", "products", "prices"], tags);
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddSingleton<IProductWriter, ProductWriter>();
        services.AddMethodCache();
        return services.BuildServiceProvider();
    }
}
