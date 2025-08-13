using System;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Demo
{
    // Demo interface with cache attributes
    public interface IDemoService
    {
        [Cache]
        Task<string> GetDataAsync(string key);

        [CacheInvalidate(Tags = new[] { "demo" })]
        Task ClearCacheAsync();
    }

    // Demo implementation
    public class DemoService : IDemoService
    {
        public virtual async Task<string> GetDataAsync(string key)
        {
            await Task.Delay(100); // Simulate work
            return $"Demo data for {key} at {DateTime.Now:HH:mm:ss.fff}";
        }

        public virtual async Task ClearCacheAsync()
        {
            Console.WriteLine("Cache cleared!");
            await Task.CompletedTask;
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== MethodCache Enhanced Service Registration Demo ===\n");

            // Demonstrate the new simplified registration
            await DemonstrateSimplifiedRegistration();

            Console.WriteLine("\n" + new string('=', 60) + "\n");

            // Demonstrate registration with options
            await DemonstrateRegistrationWithOptions();

            Console.WriteLine("\n" + new string('=', 60) + "\n");

            // Demonstrate the registration options functionality
            DemonstrateRegistrationOptions();
        }

        static async Task DemonstrateSimplifiedRegistration()
        {
            Console.WriteLine("1. Simplified Registration Demo");
            Console.WriteLine("-------------------------------");

            var services = new ServiceCollection();

            // OLD WAY (what users had to do before):
            Console.WriteLine("OLD WAY - Manual registration:");
            Console.WriteLine("  services.AddMethodCache();");
            Console.WriteLine("  services.AddSingleton<DemoService>();");
            Console.WriteLine("  services.AddIDemoServiceWithCaching(provider => provider.GetRequiredService<DemoService>());");

            Console.WriteLine("\nNEW WAY - Automatic registration:");
            Console.WriteLine("  services.AddMethodCache(null, Assembly.GetExecutingAssembly());");

            // NEW WAY - Single call does everything:
            services.AddMethodCache(config =>
            {
                config.DefaultDuration(TimeSpan.FromMinutes(5));
            }, Assembly.GetExecutingAssembly());

            var serviceProvider = services.BuildServiceProvider();

            // Verify that core services are registered
            var cacheManager = serviceProvider.GetService<ICacheManager>();
            var configuration = serviceProvider.GetService<IMethodCacheConfiguration>();
            var keyGenerator = serviceProvider.GetService<ICacheKeyGenerator>();
            var metricsProvider = serviceProvider.GetService<ICacheMetricsProvider>();

            Console.WriteLine($"\nCore services registered:");
            Console.WriteLine($"  ICacheManager: {(cacheManager != null ? "✓" : "✗")}");
            Console.WriteLine($"  IMethodCacheConfiguration: {(configuration != null ? "✓" : "✗")}");
            Console.WriteLine($"  ICacheKeyGenerator: {(keyGenerator != null ? "✓" : "✗")}");
            Console.WriteLine($"  ICacheMetricsProvider: {(metricsProvider != null ? "✓" : "✗")}");

            // Verify that concrete implementation is registered
            var demoService = serviceProvider.GetService<DemoService>();
            Console.WriteLine($"  DemoService: {(demoService != null ? "✓" : "✗")}");

            // Note: The cached interface would be registered by the source generator
            // which isn't working in this demo, but the registration logic is there
            Console.WriteLine("\nNote: Cached interface registration depends on source generator");
        }

        static async Task DemonstrateRegistrationWithOptions()
        {
            Console.WriteLine("2. Registration with Custom Options Demo");
            Console.WriteLine("----------------------------------------");

            var services = new ServiceCollection();

            // Use custom registration options
            var options = new MethodCacheRegistrationOptions
            {
                Assemblies = new[] { Assembly.GetExecutingAssembly() },
                DefaultServiceLifetime = ServiceLifetime.Singleton,
                RegisterConcreteImplementations = true,
                ThrowOnMissingImplementation = false,
                InterfaceFilter = type => type.Name.StartsWith("IDemo"),
                ImplementationFilter = type => type.Name.Contains("Demo")
            };

            services.AddMethodCache(config =>
            {
                config.DefaultDuration(TimeSpan.FromMinutes(10));
            }, options);

            var serviceProvider = services.BuildServiceProvider();

            Console.WriteLine("Custom options applied:");
            Console.WriteLine($"  Default lifetime: {options.DefaultServiceLifetime}");
            Console.WriteLine($"  Register concrete implementations: {options.RegisterConcreteImplementations}");
            Console.WriteLine($"  Interface filter: Only interfaces starting with 'IDemo'");
            Console.WriteLine($"  Implementation filter: Only classes containing 'Demo'");

            // Verify registration
            var demoService = serviceProvider.GetService<DemoService>();
            Console.WriteLine($"\nDemoService registered: {(demoService != null ? "✓" : "✗")}");
        }

        static void DemonstrateRegistrationOptions()
        {
            Console.WriteLine("3. Registration Options API Demo");
            Console.WriteLine("--------------------------------");

            // Show different ways to create registration options
            Console.WriteLine("Available registration option patterns:");

            // Default options
            var defaultOptions = MethodCacheRegistrationOptions.Default();
            Console.WriteLine($"  Default(): Scans calling assembly");

            // For specific assemblies
            var assemblyOptions = MethodCacheRegistrationOptions.ForAssemblies(
                Assembly.GetExecutingAssembly(),
                typeof(MethodCacheConfiguration).Assembly);
            Console.WriteLine($"  ForAssemblies(): Scans {assemblyOptions.Assemblies?.Length} specified assemblies");

            // For assembly containing specific type
            var typeOptions = MethodCacheRegistrationOptions.ForAssemblyContaining<Program>();
            Console.WriteLine($"  ForAssemblyContaining<T>(): Scans assembly containing specified type");

            // Custom options
            var customOptions = new MethodCacheRegistrationOptions
            {
                DefaultServiceLifetime = ServiceLifetime.Scoped,
                ScanReferencedAssemblies = true,
                InterfaceFilter = type => type.IsPublic,
                ServiceLifetimeResolver = type => type.Name.EndsWith("Service") ? ServiceLifetime.Singleton : ServiceLifetime.Scoped
            };
            Console.WriteLine($"  Custom options: Full control over registration behavior");

            Console.WriteLine("\nRegistration options provide:");
            Console.WriteLine("  ✓ Assembly scanning control");
            Console.WriteLine("  ✓ Service lifetime configuration");
            Console.WriteLine("  ✓ Interface and implementation filtering");
            Console.WriteLine("  ✓ Custom service lifetime resolution");
            Console.WriteLine("  ✓ Error handling configuration");
        }
    }
}
