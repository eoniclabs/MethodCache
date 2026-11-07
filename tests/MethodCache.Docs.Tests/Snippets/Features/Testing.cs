using Docs.GettingStarted;
using Docs.GettingStarted.After;
using ProductService = Docs.GettingStarted.After.ProductService;
using MethodCache;
using MethodCache.Extensibility;
using MethodCache.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Docs.Testing
{
    public static class Examples
    {
        public static IServiceProvider WithCaching()
        {
            // begin-snippet: testing-with-caching
            var services = new ServiceCollection();
            services.AddSingleton<Database>();
            services.AddScoped<IProductService, ProductService>();
            services.AddMethodCache();
            var provider = services.BuildServiceProvider();

            // Resolved through the generated decorator, exactly as in the application
            var products = provider.CreateScope().ServiceProvider.GetRequiredService<IProductService>();
            // end-snippet
            return provider;
        }

        public static async Task WriteDirectly(IServiceProvider provider, Database database, Product changed)
        {
            // begin-snippet: testing-direct-write
            await database.SaveAsync(changed);   // the test changes the data behind the cache's back

            // Remove everything cached...
            await provider.GetRequiredService<ICacheAdministrator>().ClearAsync();

            // ...or only what the write changed
            var cache = provider.GetRequiredService<ICacheManager>();
            var keys = provider.GetRequiredService<IMethodCacheKeys>();
            await cache.InvalidateTagAsync(keys.Tag<IProductService>(p => p.GetAsync(changed.Id, default), "product:{id}"));
            // end-snippet
        }

        public static async Task RecordAsync()
        {
            // begin-snippet: testing-recorder
            var services = new ServiceCollection();
            services.AddSingleton<Database>();
            services.AddScoped<IProductService, ProductService>();
            services.AddMethodCache();
            services.AddMethodCacheTesting();   // a fake clock and a recorder
            await using var provider = services.BuildServiceProvider();
            var recorder = provider.GetRequiredService<CacheRecorder>();
            var time = provider.GetRequiredService<FakeTimeProvider>();
            var products = provider.CreateScope().ServiceProvider.GetRequiredService<IProductService>();

            await products.GetAsync(42);
            await products.GetAsync(42);
            Assert.Equal(1, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));   // the second call was a hit

            await products.UpdateAsync(42, new Product(42, "Green tea", 5m));
            Assert.Contains("product:42", recorder.InvalidatedTags);
            await products.GetAsync(42);
            Assert.Equal(2, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));

            time.Advance(TimeSpan.FromMinutes(11));   // past the Duration of 10 minutes
            await products.GetAsync(42);
            Assert.Equal(3, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));
            // end-snippet
        }

        public static async Task CheckServicesAsync(IServiceProvider provider)
        {
            // begin-snippet: testing-cached-services
            // Every service with cached methods resolves, and to its generated caching decorator
            Assert.Empty(await CachedServices.ProblemsAsync(provider));
            // end-snippet
        }

        public static void CheckRegistrations(IServiceProvider provider)
        {
            // begin-snippet: testing-cached-service-registrations
            // Every service with cached methods is registered before AddMethodCache; resolves none of them
            Assert.Empty(CachedServices.RegistrationProblems(provider));
            // end-snippet
        }

        public static void CheckTags(IServiceProvider provider)
        {
            // begin-snippet: testing-tag-coverage
            // Every tag of a cached method has a [CacheInvalidate] method, an EF Core mapping or an invalidation in a referenced project
            Assert.Empty(TagCoverage.Uninvalidated(provider));
            // end-snippet
        }

        public static IServiceProvider WithoutCaching()
        {
            // begin-snippet: testing-without-caching
            var services = new ServiceCollection();
            services.AddSingleton<Database>();
            services.AddScoped<IProductService, ProductService>();
            services.AddMethodCache();
            services.AddSingleton<ICacheManager, NoOpCacheManager>();   // after AddMethodCache: every call reaches ProductService
            // end-snippet
            return services.BuildServiceProvider();
        }

        public static void ReplaceWithFake(IWebHostBuilder builder)
        {
            // begin-snippet: testing-replace-cached-service
            // In a WebApplicationFactory: ConfigureTestServices runs after the app's AddMethodCache()
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IProductService, FakeProductService>();
                services.AddMethodCache();   // again, so the fake gets a decorator like the service it replaces
            });
            // end-snippet
        }
    }

    public sealed class FakeProductService : IProductService
    {
        public Task<Product> GetAsync(int id, CancellationToken ct = default) => Task.FromResult(new Product(id, "Test tea", 1m));

        public Task<List<Product>> SearchAsync(string text, int page) => Task.FromResult(new List<Product>());

        public Task UpdateAsync(int id, Product product) => Task.CompletedTask;
    }
}
