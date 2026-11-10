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
using System.Text.Json;
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

        public static void CheckRegisteredServices(IServiceProvider provider)
        {
            // begin-snippet: testing-cached-services-registered
            // The application registers exactly these services with cached methods, none forgotten
            Assert.Equal([typeof(IProductService)], CachedServices.Registered(provider));
            // end-snippet
        }

        public static void CheckDeclaredServices(IServiceProvider provider)
        {
            // begin-snippet: testing-cached-services-declared
            // Every interface with cached methods in the application's project is registered
            Assert.Equal(CachedServices.Declared(typeof(IProductService).Assembly), CachedServices.Registered(provider));
            // end-snippet
        }

        public sealed class TenancyModule;

        public sealed class BillingModule;

        public interface ICurrencyRates;

        public static void CheckModuleServices(IServiceProvider provider)
        {
            // begin-snippet: testing-cached-services-modules
            // The cached interfaces are in the modules the application is built from: pass each module's assembly
            Assert.Equal(
                CachedServices.Declared(typeof(TenancyModule).Assembly, typeof(BillingModule).Assembly),
                CachedServices.Registered(provider));
            // end-snippet
        }

        public static void CheckSharedLibraryServices(IServiceProvider provider)
        {
            // begin-snippet: testing-cached-services-shared
            // The application's own cached interfaces, and the one it uses from a shared library that declares others
            var expected = CachedServices.Declared(typeof(IProductService).Assembly)
                .Append(typeof(ICurrencyRates))
                .OrderBy(type => type.FullName, StringComparer.Ordinal);
            Assert.Equal(expected, CachedServices.Registered(provider));
            // end-snippet
        }

        public static async Task OtherTenantAsync(IServiceProvider provider, Guid tenantA, Guid tenantB)
        {
            // begin-snippet: testing-other-tenant
            await using var scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<RequestTenant>().Id = tenantA;   // the request is tenant A's
            var projects = scope.ServiceProvider.GetRequiredService<IProjects>();

            // A load for tenant B fails in the check, so nothing is cached under B's key
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projects.ListAsync(tenantB));
            // end-snippet
        }

        public static void CheckPolicy(IServiceProvider provider)
        {
            // begin-snippet: testing-method-policy
            // How ListAsync is cached, from its attribute, startup code and configuration together
            var list = CachedServices.Method<IProjects>(provider, p => p.ListAsync(Guid.Empty));
            Assert.Equal(TimeSpan.FromMinutes(5), list.Lifetime.Duration);
            Assert.Equal(["tenantId"], list.KeyParameters);
            Assert.Empty(list.ReplacedSources);   // no source replaces what another one sets for it
            // end-snippet
        }

        public static string PoliciesAsJson(IServiceProvider provider)
        {
            // begin-snippet: testing-methods-json
            // Every method's policy as JSON: compare it with a file in the repository, so a change shows up in review
            var policies = JsonSerializer.Serialize(provider.GetRequiredService<ICacheAdministrator>().GetMethods(),
                new JsonSerializerOptions { WriteIndented = true });
            // end-snippet
            return policies;
        }

        public static void CheckTenantKeys(IServiceProvider provider)
        {
            // begin-snippet: testing-tenant-keys
            // The methods that serve every tenant, such as an API key lookup, by MethodId
            string[] everyTenant = [];
            foreach (var method in provider.GetRequiredService<ICacheAdministrator>().GetMethods())
            {
                if (everyTenant.Contains(method.MethodId)) continue;
                Assert.True(method.KeyParameters is ["tenantId", ..], $"{method.MethodId} doesn't key on the tenant first.");
            }
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

    public interface IProjects
    {
        [Cache(Duration = "5m")]
        Task<List<string>> ListAsync(Guid tenantId);
    }

    public sealed class RequestTenant
    {
        public Guid Id { get; set; }

        [ChecksRequestState]
        public void EnsureActsFor(Guid tenantId)
        {
            if (tenantId != Id) throw new UnauthorizedAccessException("Not the request's tenant.");
        }
    }

    public sealed class Projects(RequestTenant tenant) : IProjects
    {
        public Task<List<string>> ListAsync(Guid tenantId)
        {
            tenant.EnsureActsFor(tenantId);
            return Task.FromResult(new List<string> { $"project of {tenantId}" });
        }
    }

    public sealed class FakeProductService : IProductService
    {
        public Task<Product> GetAsync(int id, CancellationToken ct = default) => Task.FromResult(new Product(id, "Test tea", 1m));

        public Task<List<Product>> SearchAsync(string text, int page) => Task.FromResult(new List<Product>());

        public Task UpdateAsync(int id, Product product) => Task.CompletedTask;
    }
}
