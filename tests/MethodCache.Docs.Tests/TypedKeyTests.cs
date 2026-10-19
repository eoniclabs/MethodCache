using MethodCache.Docs.Tests.TypedKeys;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests
{
    /// <summary>
    /// <see cref="IMethodCacheKeys"/> returns the key the decorator stores a call under, for every kind of method
    /// (docs/features/keys.md), without running the method.
    /// </summary>
    public class TypedKeyTests
    {
        [Fact]
        public async Task For_ReturnsTheKeyTheDecoratorStores_ForEveryKindOfMethod()
        {
            await using var provider = Build();
            var catalog = provider.GetRequiredService<ICatalog>();
            await catalog.Find(1);
            await catalog.Find("A-1");
            await catalog.CountAsync("books");
            catalog.Size(3);
            await catalog.Load<string>(4);
            await catalog.Search(new Dictionary<string, int> { ["age"] = 30 });

            var stored = provider.GetRequiredService<ICacheAdministrator>().ListEntries().Select(e => e.StoredKey).ToList();
            var keys = provider.GetRequiredService<IMethodCacheKeys>();
            var expected = new[]
            {
                keys.For<ICatalog>(c => c.Find(1)),
                keys.For<ICatalog>(c => c.Find("A-1")),
                keys.For<ICatalog>(c => c.CountAsync("books")),
                keys.For<ICatalog>(c => c.Size(3)),
                keys.For<ICatalog>(c => c.Load<string>(4)),
                keys.For<ICatalog>(c => c.Search(new Dictionary<string, int> { ["age"] = 30 }))
            };

            Assert.Equal(stored.Order(StringComparer.Ordinal), expected.Order(StringComparer.Ordinal));
            Assert.Contains("[MethodCache.Docs.Tests]MethodCache.Docs.Tests.TypedKeys.ICatalog.Find(int):1", expected);
            Assert.Contains("[MethodCache.Docs.Tests]MethodCache.Docs.Tests.TypedKeys.ICatalog.Find(string):A-1", expected);
            Assert.Contains("[MethodCache.Docs.Tests]MethodCache.Docs.Tests.TypedKeys.ICatalog.CountAsync:v3:books", expected);
            Assert.Equal(6, provider.GetRequiredService<Catalog>().Loads);
        }

        [Fact]
        public async Task For_RunsNothing_AndItsKeyInvalidatesTheEntry()
        {
            await using var provider = Build();
            var catalog = provider.GetRequiredService<ICatalog>();
            var counter = provider.GetRequiredService<Catalog>();
            var cache = provider.GetRequiredService<ICacheManager>();
            var keys = provider.GetRequiredService<IMethodCacheKeys>();

            await catalog.Find(1);
            var key = keys.For<ICatalog>(c => c.Find(1));
            await catalog.Find(1);
            Assert.Equal(1, counter.Loads);

            await cache.InvalidateKeyAsync(key);
            await catalog.Find(1);
            Assert.Equal(2, counter.Loads);
        }

        [Fact]
        public async Task For_FollowsARuntimeVersionChange()
        {
            await using var provider = Build();
            var keys = provider.GetRequiredService<IMethodCacheKeys>();
            Assert.EndsWith(".Find(int):1", keys.For<ICatalog>(c => c.Find(1)), StringComparison.Ordinal);

            await provider.GetRequiredService<IRuntimeCacheConfigurator>()
                .UpsertAsync<ICatalog, Task<string>>(c => c.Find(Any<int>.Value), o => o.WithVersion(5));

            Assert.EndsWith(".Find(int):v5:1", keys.For<ICatalog>(c => c.Find(1)), StringComparison.Ordinal);
        }

        [Fact]
        public async Task For_PutsTheServiceKeyOfAKeyedRegistrationInTheKey()
        {
            var services = new ServiceCollection();
            services.AddKeyedSingleton<IRates, Rates>("ecb");
            services.AddKeyedSingleton<IRates, Rates>("fed");
            services.AddMethodCache();
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredKeyedService<IRates>("ecb").Get("EUR");

            var keys = provider.GetRequiredService<IMethodCacheKeys>();
            var ecb = keys.For<IRates>(r => r.Get("EUR"), serviceKey: "ecb");
            Assert.EndsWith(".IRates.Get@ecb:EUR", ecb, StringComparison.Ordinal);
            Assert.EndsWith(".IRates.Get@fed:EUR", keys.For<IRates>(r => r.Get("EUR"), serviceKey: "fed"), StringComparison.Ordinal);
            Assert.Contains(ecb, provider.GetRequiredService<ICacheAdministrator>().ListEntries().Select(e => e.StoredKey));
            Assert.Throws<InvalidOperationException>(() => keys.For<IRates>(r => r.Get("EUR")));
        }

        [Fact]
        public async Task For_RejectsCallsThatAreNotOneCachedMethod()
        {
            await using var provider = Build();
            var keys = provider.GetRequiredService<IMethodCacheKeys>();

            Assert.Throws<InvalidOperationException>(() => keys.For<ICatalog>(c => c.NotCachedAsync(1)));
            Assert.Throws<InvalidOperationException>(() => keys.For<ICatalog>(c => c.ResetAsync()));
            Assert.Throws<InvalidOperationException>(() => keys.For<ICatalog>(_ => null));
            Assert.Throws<InvalidOperationException>(() => keys.For<ICatalog>(c => (c.Find(1), c.Find(2))));
            Assert.Throws<InvalidOperationException>(() => keys.For<IRates>(r => r.Get("EUR")));
            Assert.Throws<ArgumentNullException>(() => keys.For<ICatalog>(null!));
            Assert.Equal(0, provider.GetRequiredService<Catalog>().Loads);
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddSingleton<Catalog>();
            services.AddSingleton<ICatalog>(sp => sp.GetRequiredService<Catalog>());
            services.AddMethodCache();
            return services.BuildServiceProvider();
        }
    }
}

namespace MethodCache.Docs.Tests.TypedKeys
{
    public interface ICatalog
    {
        [Cache] Task<string> Find(int id);
        [Cache] Task<string> Find(string code);
        [Cache(Version = 3)] ValueTask<int> CountAsync(string category);
        [Cache] int Size(int id);
        [Cache] Task<T?> Load<T>(int id) where T : class;
        [Cache] Task<int> Search(Dictionary<string, int> filter);
        Task<int> NotCachedAsync(int id);
        [CacheInvalidate(Tags = ["catalog"])] Task ResetAsync();
    }

    public sealed class Catalog : ICatalog
    {
        public int Loads;
        public Task<string> Find(int id) { Loads++; return Task.FromResult("id"); }
        public Task<string> Find(string code) { Loads++; return Task.FromResult(code); }
        public ValueTask<int> CountAsync(string category) { Loads++; return ValueTask.FromResult(1); }
        public int Size(int id) { Loads++; return id; }
        public Task<T?> Load<T>(int id) where T : class { Loads++; return Task.FromResult<T?>(null); }
        public Task<int> Search(Dictionary<string, int> filter) { Loads++; return Task.FromResult(filter.Count); }
        public Task<int> NotCachedAsync(int id) { Loads++; return Task.FromResult(id); }
        public Task ResetAsync() => Task.CompletedTask;
    }

    public interface IRates
    {
        [Cache] Task<decimal> Get(string currency);
    }

    public sealed class Rates : IRates
    {
        public Task<decimal> Get(string currency) => Task.FromResult(1m);
    }
}
