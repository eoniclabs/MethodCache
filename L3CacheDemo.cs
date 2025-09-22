using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Providers.SqlServer.Extensions;

namespace MethodCacheDemo;

// Example service that uses caching
public class ProductService
{
    [Cache(TimeSpan.FromMinutes(30), Tags = ["products"])]
    public async Task<Product> GetProductAsync(int id)
    {
        // Simulate expensive database call
        await Task.Delay(100);
        return new Product { Id = id, Name = $"Product {id}", Price = id * 10.99m };
    }

    [Cache(TimeSpan.FromHours(4), Tags = ["reports", "analytics"])]
    public async Task<SalesReport> GenerateSalesReportAsync(DateTime date)
    {
        // Simulate expensive report generation
        await Task.Delay(500);
        return new SalesReport
        {
            Date = date,
            TotalSales = Random.Shared.Next(1000, 10000),
            OrderCount = Random.Shared.Next(50, 200)
        };
    }
}

public record Product(int Id, string Name, decimal Price);
public record SalesReport(DateTime Date, decimal TotalSales, int OrderCount);

class Program
{
    static async Task Main(string[] args)
    {
        // Setup the new unified L3 cache architecture
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

        // Example 1: L1 + L3 setup (Memory + SQL Server Persistent)
        services.AddL1L3CacheWithSqlServer(
            configureStorage: options =>
            {
                options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
                options.L3DefaultExpiration = TimeSpan.FromDays(7);
                options.L3Enabled = true;
                options.EnableL3Promotion = true;
                options.EnableAsyncL3Writes = true;
            },
            configureSqlServer: options =>
            {
                options.ConnectionString = "Server=localhost;Database=CacheDemo;Trusted_Connection=true;";
                options.Schema = "cache";
                options.EnableAutoTableCreation = true;
            });

        // Alternative: Full L1 + L2 + L3 setup
        /*
        services.AddTripleLayerCacheWithSqlServer(
            configureStorage: options =>
            {
                options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
                options.L2DefaultExpiration = TimeSpan.FromHours(2);
                options.L3DefaultExpiration = TimeSpan.FromDays(30);
                options.EnableL3Promotion = true;
            },
            configureSqlServer: options =>
            {
                options.ConnectionString = connectionString;
                options.EnableBackplane = true;
            });
        */

        services.AddMethodCache();
        services.AddScoped<ProductService>();

        var serviceProvider = services.BuildServiceProvider();
        var productService = serviceProvider.GetRequiredService<ProductService>();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("=== L3 Cache Demo ===");

        // First call - cache miss, will populate L1 and L3
        logger.LogInformation("First call to GetProductAsync(1)...");
        var product1 = await productService.GetProductAsync(1);
        logger.LogInformation("Retrieved: {Product}", product1);

        // Second call - should hit L1 cache
        logger.LogInformation("Second call to GetProductAsync(1)...");
        var product2 = await productService.GetProductAsync(1);
        logger.LogInformation("Retrieved: {Product}", product2);

        // Generate a report - long-term cache in L3
        logger.LogInformation("Generating sales report...");
        var report = await productService.GenerateSalesReportAsync(DateTime.Today);
        logger.LogInformation("Report: {Report}", report);

        // Demo cache stats
        var hybridManager = serviceProvider.GetRequiredService<HybridStorageManager>();
        var stats = await hybridManager.GetStatsAsync();

        logger.LogInformation("=== Cache Statistics ===");
        logger.LogInformation("L1 Hits: {L1Hits}", stats.AdditionalStats["L1Hits"]);
        logger.LogInformation("L1 Misses: {L1Misses}", stats.AdditionalStats["L1Misses"]);
        logger.LogInformation("L3 Hits: {L3Hits}", stats.AdditionalStats["L3Hits"]);
        logger.LogInformation("L3 Misses: {L3Misses}", stats.AdditionalStats["L3Misses"]);
        logger.LogInformation("Total Hit Ratio: {HitRatio:P2}", stats.AdditionalStats["TotalHitRatio"]);

        await serviceProvider.DisposeAsync();
        logger.LogInformation("Demo completed!");
    }
}