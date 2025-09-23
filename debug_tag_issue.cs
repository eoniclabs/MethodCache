using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Infrastructure.Implementation;
using MethodCache.Providers.SqlServer.IntegrationTests.Tests;

// Simple test to debug the tag removal issue
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

var connectionString = "Server=localhost,1433;Database=MethodCacheTests;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=true;Encrypt=false;";
var schema = $"debug_tag_{Guid.NewGuid():N}".Replace("-", "");

services.AddSqlServerHybridInfrastructureForTests(options =>
{
    options.ConnectionString = connectionString;
    options.EnableAutoTableCreation = true;
    options.Schema = schema;
});

var serviceProvider = services.BuildServiceProvider();

// Initialize tables
var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
await tableManager.EnsureTablesExistAsync();

var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();

var key = "debug-tag-test";
var value = "debug-tag-value";
var tag = "debug-tag";

Console.WriteLine("1. Setting value with tag...");
await hybridStorage.SetAsync(key, value, TimeSpan.FromMinutes(10), new[] { tag });

Console.WriteLine("2. Getting value before removal...");
var beforeRemoval = await hybridStorage.GetAsync<string>(key);
Console.WriteLine($"Before removal: {beforeRemoval ?? "null"}");

Console.WriteLine("3. Removing by tag...");
await hybridStorage.RemoveByTagAsync(tag);

Console.WriteLine("4. Getting value after removal...");
var afterRemoval = await hybridStorage.GetAsync<string>(key);
Console.WriteLine($"After removal: {afterRemoval ?? "null"}");

Console.WriteLine("5. Checking L1 memory storage directly...");
var memoryStorage = serviceProvider.GetRequiredService<MethodCache.Infrastructure.Abstractions.IMemoryStorage>();
var memoryValue = await memoryStorage.GetAsync<string>(key);
Console.WriteLine($"L1 Memory value: {memoryValue ?? "null"}");

Console.WriteLine("6. Checking L2/L3 SqlServer storage directly...");
var sqlStorage = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Infrastructure.SqlServerPersistentStorageProvider>();
var sqlValue = await sqlStorage.GetAsync<string>(key);
Console.WriteLine($"L2/L3 SQL value: {sqlValue ?? "null"}");

if (afterRemoval != null)
{
    Console.WriteLine("ERROR: Value still exists after tag removal!");
}
else
{
    Console.WriteLine("SUCCESS: Value correctly removed by tag");
}

await serviceProvider.DisposeAsync();