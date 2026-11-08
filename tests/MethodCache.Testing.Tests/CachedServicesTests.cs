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
    public async Task Problems_EveryServiceResolvesToItsDecorator_IsEmpty()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddScoped<IProductWriter, ProductWriter>();
        services.AddKeyedTransient<IImportedPrices, ImportedPrices>("import");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.Empty(await CachedServices.ProblemsAsync(provider));
    }

    // Implements only IAsyncDisposable, which a scope disposed synchronously refuses
    public sealed class AsyncOnlyConnection : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public sealed class ConnectedProducts(AsyncOnlyConnection connection) : IProducts
    {
        public Task<string> GetAsync(int id) => Task.FromResult($"product {id} {connection.Disposed}");
        public Task<decimal> PriceAsync(int id) => Task.FromResult(1m);
    }

    public sealed class AsyncOnlyProducts : IProducts, IAsyncDisposable
    {
        public Task<string> GetAsync(int id) => Task.FromResult($"product {id}");
        public Task<decimal> PriceAsync(int id) => Task.FromResult(1m);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Problems_AScopedDependencyThatOnlyDisposesAsynchronously_IsNoProblem()
    {
        var services = new ServiceCollection();
        services.AddScoped<AsyncOnlyConnection>();
        services.AddScoped<IProducts, ConnectedProducts>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        Assert.Empty(await CachedServices.ProblemsAsync(provider));
    }

    [Fact]
    public async Task Problems_AScopedServiceThatOnlyDisposesAsynchronously_IsNoProblem()
    {
        var services = new ServiceCollection();
        services.AddScoped<IProducts, AsyncOnlyProducts>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        Assert.Empty(await CachedServices.ProblemsAsync(provider));
    }

    [Fact]
    public async Task Problems_AServiceRegisteredAfterAddMethodCache_IsReported()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProductWriter, ProductWriter>();
        services.AddMethodCache();
        services.AddSingleton<IProducts, Products>();
        using var provider = services.BuildServiceProvider();

        var problem = Assert.Single(await CachedServices.ProblemsAsync(provider));

        Assert.Equal(typeof(IProducts), problem.Service);
        Assert.Null(problem.ServiceKey);
        Assert.Contains("was registered after AddMethodCache and has no generated decorator", problem.Problem, StringComparison.Ordinal);
        Assert.StartsWith($"{typeof(IProducts)}: Cacheable service", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Problems_AServiceThatCantBeResolved_IsReportedWithTheError()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IProducts, SourcedProducts>("sourced");
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        var problem = Assert.Single(await CachedServices.ProblemsAsync(provider));

        Assert.Equal("sourced", problem.ServiceKey);
        Assert.StartsWith("Resolving it throws InvalidOperationException:", problem.Problem, StringComparison.Ordinal);
        Assert.Contains(nameof(IPriceSource), problem.Problem, StringComparison.Ordinal);
        Assert.StartsWith($"{typeof(IProducts)} (key sourced): Resolving it throws", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Problems_WithNoServiceWithCachedMethods_Throws()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CachedServices.ProblemsAsync(provider));

        Assert.Contains("nothing was checked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationProblems_EveryServiceRegisteredBeforeAddMethodCache_IsEmptyWithoutResolvingThem()
    {
        var services = new ServiceCollection();
        // Resolving it needs an IPriceSource, as a service may need a database the test doesn't have
        services.AddSingleton<IProducts, SourcedProducts>();
        services.AddScoped<IProductWriter, ProductWriter>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        Assert.Empty(CachedServices.RegistrationProblems(provider));
        Assert.Single(await CachedServices.ProblemsAsync(provider));
    }

    [Fact]
    public async Task RegistrationProblems_AServiceRegisteredAfterAddMethodCache_IsReported()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProductWriter, ProductWriter>();
        services.AddMethodCache();
        services.AddSingleton<IProducts, SourcedProducts>();
        await using var provider = services.BuildServiceProvider();

        var problem = Assert.Single(CachedServices.RegistrationProblems(provider));

        Assert.Equal(typeof(IProducts), problem.Service);
        Assert.Contains("was registered after AddMethodCache", problem.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationProblems_WithNoServiceWithCachedMethods_Throws()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => CachedServices.RegistrationProblems(provider));

        Assert.Contains("nothing was checked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_ListsEachServiceWithCachedMethodsOnce_ByFullName()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddScoped<IProductWriter, ProductWriter>();
        services.AddKeyedTransient<IImportedPrices, ImportedPrices>("import");
        services.AddKeyedTransient<IImportedPrices, ImportedPrices>("backfill");
        services.AddSingleton<IPriceSource>(new FixedPrice()); // no cached methods
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        Assert.Equal([typeof(IImportedPrices), typeof(IProductWriter), typeof(IProducts)], CachedServices.Registered(provider));
    }

    [Fact]
    public async Task Registered_AServiceNeverRegistered_IsMissingFromTheList()
    {
        // ProblemsAsync can't see a service the application forgot to register; comparing the list can
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        Assert.Empty(await CachedServices.ProblemsAsync(provider));
        Assert.DoesNotContain(typeof(IProductWriter), CachedServices.Registered(provider));
    }

    [Fact]
    public async Task Registered_WithNoServiceWithCachedMethods_Throws()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => CachedServices.Registered(provider));

        Assert.Contains("nothing was checked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_ListsTheAssemblysServicesWithCachedMethodsOnce_ByFullName()
    {
        var declared = CachedServices.Declared(typeof(IProducts).Assembly, typeof(IProducts).Assembly);

        Assert.Subset(declared.ToHashSet(), new HashSet<Type> { typeof(IFoodCatalog), typeof(IImportedPrices), typeof(IProductWriter), typeof(IProducts) });
        Assert.DoesNotContain(typeof(IPriceSource), declared);
        Assert.Equal(declared.Distinct().OrderBy(static type => type.FullName, StringComparer.Ordinal), declared);
    }

    [Fact]
    public async Task Declared_ComparedWithRegistered_FindsAServiceNeverRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddScoped<IProductWriter, ProductWriter>();
        services.AddKeyedTransient<IImportedPrices, ImportedPrices>("import");
        services.AddMethodCache();   // IFoodCatalog is forgotten
        await using var provider = services.BuildServiceProvider();

        var missing = CachedServices.Declared(typeof(IProducts).Assembly).Except(CachedServices.Registered(provider)).ToList();

        Assert.Contains(typeof(IFoodCatalog), missing);
        Assert.DoesNotContain(typeof(IProducts), missing);
        Assert.DoesNotContain(typeof(IImportedPrices), missing);
    }

    [Fact]
    public void Declared_AnAssemblyWithNoCachedService_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CachedServices.Declared(typeof(object).Assembly));

        Assert.Contains("declare no service with cached methods", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_WithNoAssembly_Throws()
    {
        Assert.Throws<ArgumentException>(() => CachedServices.Declared());
    }

    private sealed class FixedPrice : IPriceSource
    {
        public decimal Price(int id) => 1m;
    }

    [Fact]
    public async Task Problems_WithoutAddMethodCache_Throws()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CachedServices.ProblemsAsync(provider));

        Assert.Contains("AddMethodCache", error.Message, StringComparison.Ordinal);
    }
}
