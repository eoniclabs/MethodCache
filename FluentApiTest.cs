using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Extensions;
using MethodCache.Core.Storage;
using MethodCache.Providers.SqlServer.Extensions;

namespace MethodCache.Test;

/// <summary>
/// Simple test to validate the new fluent API works as expected.
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Testing new MethodCache fluent API...");

        try
        {
            // Test 1: Basic L1 only configuration
            var services1 = new ServiceCollection();
            services1.AddMethodCache()
                     .WithL1(Memory.Default());
            var provider1 = services1.BuildServiceProvider();
            Console.WriteLine("✓ L1 only configuration works");

            // Test 2: L1 with custom configuration
            var services2 = new ServiceCollection();
            services2.AddMethodCache()
                     .WithL1(Memory.Configure(opts =>
                     {
                         opts.MaxSize = 5000;
                         opts.DefaultExpiration = TimeSpan.FromMinutes(10);
                     }));
            var provider2 = services2.BuildServiceProvider();
            Console.WriteLine("✓ L1 with custom configuration works");

            // Test 3: L1 + L3 configuration
            var services3 = new ServiceCollection();
            services3.AddMethodCache()
                     .WithL1(Memory.Default())
                     .WithL3SqlServer("Server=localhost;Database=TestCache;", configure =>
                     {
                         configure.Schema = "dbo";
                         configure.EnableAutoTableCreation = true;
                     });
            var provider3 = services3.BuildServiceProvider();
            Console.WriteLine("✓ L1 + L3 SqlServer configuration works");

            // Test 4: Using the fluent factory methods
            var sqlServerProvider = MethodCache.Providers.SqlServer.Storage.SqlServer
                .FromConnectionString("Server=localhost;Database=TestCache;")
                .WithSchema("cache")
                .WithAutoTableCreation(true)
                .WithCleanupSchedule(TimeSpan.FromHours(1))
                .WithBackplane(true);

            var services4 = new ServiceCollection();
            services4.AddMethodCache()
                     .WithL1(Memory.WithMaxSize(1000))
                     .WithL3(sqlServerProvider);
            var provider4 = services4.BuildServiceProvider();
            Console.WriteLine("✓ Fluent SqlServer provider configuration works");

            Console.WriteLine("\n🎉 All tests passed! New fluent API is working correctly.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Test failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}