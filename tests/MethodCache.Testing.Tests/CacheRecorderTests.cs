using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.Testing.Tests;

public class CacheRecorderTests
{
    [Fact]
    public async Task Loads_CountsTheRunsOfACall()
    {
        await using var provider = Build();
        var products = provider.GetRequiredService<IProducts>();
        var recorder = provider.GetRequiredService<CacheRecorder>();

        await products.GetAsync(42);
        await products.GetAsync(42);
        await products.GetAsync(7);

        Assert.Equal(1, recorder.Loads<IProducts>(p => p.GetAsync(42)));
        Assert.Equal(1, recorder.Loads<IProducts>(p => p.GetAsync(7)));
        Assert.Equal(0, recorder.Loads<IProducts>(p => p.GetAsync(1)));
        Assert.Equal(0, recorder.Loads<IProducts>(p => p.PriceAsync(42)));
    }

    [Fact]
    public async Task InvalidatedTags_ListsTheTagsOfACacheInvalidateMethod_AndTheCallReloads()
    {
        await using var provider = Build();
        var products = provider.GetRequiredService<IProducts>();
        var recorder = provider.GetRequiredService<CacheRecorder>();
        await products.GetAsync(42);

        await provider.GetRequiredService<IProductWriter>().RenameAsync(42, "Tea");
        await products.GetAsync(42);

        Assert.Equivalent(new[] { "product:42", "products" }, recorder.InvalidatedTags, strict: true);
        Assert.Equal(2, recorder.Loads<IProducts>(p => p.GetAsync(42)));
    }

    [Fact]
    public async Task InvalidatedTagsAndKeys_ListCacheManagerCalls_InOrder()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<CacheRecorder>();
        var cache = provider.GetRequiredService<ICacheManager>();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();
        var key = keys.For<IProducts>(p => p.GetAsync(42));

        await cache.InvalidateTagAsync("prices");
        await cache.InvalidateTagsAsync(["products", "prices"]);
        await cache.InvalidateKeyAsync(key);

        Assert.Equal(["prices", "products", "prices"], recorder.InvalidatedTags);
        Assert.Equal([key], recorder.InvalidatedKeys);
    }

    [Fact]
    public async Task Loads_ByKey_CountsAManualEntry()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<CacheRecorder>();
        var cache = provider.GetRequiredService<ICacheManager>();

        await cache.GetOrCreateAsync("rates:sek", _ => ValueTask.FromResult(11.2m), options => options.WithDuration(TimeSpan.FromMinutes(5)));
        await cache.GetOrCreateAsync("rates:sek", _ => ValueTask.FromResult(11.2m), options => options.WithDuration(TimeSpan.FromMinutes(5)));

        Assert.Equal(1, recorder.Loads("rates:sek"));
    }

    [Fact]
    public async Task Clear_ForgetsWhatWasRecorded()
    {
        await using var provider = Build();
        var products = provider.GetRequiredService<IProducts>();
        var recorder = provider.GetRequiredService<CacheRecorder>();
        await products.GetAsync(42);
        await provider.GetRequiredService<ICacheManager>().InvalidateTagAsync("products");

        recorder.Clear();

        Assert.Equal(0, recorder.Loads<IProducts>(p => p.GetAsync(42)));
        Assert.Empty(recorder.InvalidatedTags);
        Assert.Empty(recorder.InvalidatedKeys);
    }

    [Fact]
    public async Task FakeTime_ExpiresAnEntryWhenTheTestAdvancesIt()
    {
        await using var provider = Build();
        var products = provider.GetRequiredService<IProducts>();
        var recorder = provider.GetRequiredService<CacheRecorder>();
        var time = provider.GetRequiredService<FakeTimeProvider>();

        await products.GetAsync(42);
        time.Advance(TimeSpan.FromMinutes(9));
        await products.GetAsync(42);
        Assert.Equal(1, recorder.Loads<IProducts>(p => p.GetAsync(42)));

        time.Advance(TimeSpan.FromMinutes(2));
        await products.GetAsync(42);
        Assert.Equal(2, recorder.Loads<IProducts>(p => p.GetAsync(42)));
    }

    [Fact]
    public async Task AddMethodCacheTesting_BeforeAddMethodCache_RecordsToo()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddMethodCacheTesting();
        services.AddSingleton<IProducts, Products>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IProducts>().GetAsync(42);

        Assert.Same(provider.GetRequiredService<FakeTimeProvider>(), provider.GetRequiredService<TimeProvider>());
        Assert.Equal(1, provider.GetRequiredService<CacheRecorder>().Loads<IProducts>(p => p.GetAsync(42)));
    }

    [Fact]
    public void Loads_OfAMethodThatIsNotCached_Throws()
    {
        using var provider = Build();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<CacheRecorder>().Loads<IProductWriter>(w => w.RenameAsync(1, "Tea")));
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProducts, Products>();
        services.AddSingleton<IProductWriter, ProductWriter>();
        services.AddMethodCache();
        services.AddMethodCacheTesting();
        return services.BuildServiceProvider();
    }
}
