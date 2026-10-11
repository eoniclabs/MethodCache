using Docs.GettingStarted;
using Docs.GettingStarted.After;
using ProductService = Docs.GettingStarted.After.ProductService;
using MethodCache;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;

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
    }
}
