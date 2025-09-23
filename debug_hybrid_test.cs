using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Infrastructure.Implementation;
using MethodCache.Providers.SqlServer.IntegrationTests.Tests;

// Simple test to debug the hybrid storage issue
var services = new ServiceCollection();
services.AddLogging();

var connectionString = "Server=localhost,1433;Database=MethodCacheTests;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=true;Encrypt=false;";
var schema = $"debug_{Guid.NewGuid():N}".Replace("-", "");

services.AddSqlServerHybridInfrastructureForTests(options =>
{
    options.ConnectionString = connectionString;
    options.EnableAutoTableCreation = true;
    options.EnableBackplane = false; // Disable backplane for simpler test
    options.Schema = schema;
    options.KeyPrefix = "debug:";
}, configureStorage: storageOptions =>
{
    storageOptions.EnableAsyncL2Writes = false;
});

var serviceProvider = services.BuildServiceProvider();

// Initialize tables
var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
await tableManager.EnsureTablesExistAsync();

var hybridStorage = serviceProvider.GetRequiredService<HybridStorageManager>();

var key = "test-key";
var value = "test-value";
var tag = "test-tag";

Console.WriteLine("1. Setting value...");
await hybridStorage.SetAsync(key, value, TimeSpan.FromMinutes(10), new[] { tag });

Console.WriteLine("2. Getting value...");
var retrieved = await hybridStorage.GetAsync<string>(key);
Console.WriteLine($"Retrieved: {retrieved}");

Console.WriteLine("3. Removing by tag...");
await hybridStorage.RemoveByTagAsync(tag);

Console.WriteLine("4. Getting value after removal...");
var afterRemoval = await hybridStorage.GetAsync<string>(key);
Console.WriteLine($"After removal: {afterRemoval ?? "null"}");

if (afterRemoval != null)
{
    Console.WriteLine("ERROR: Value still exists after removal!");
}
else
{
    Console.WriteLine("SUCCESS: Value correctly removed");
}

await serviceProvider.DisposeAsync();