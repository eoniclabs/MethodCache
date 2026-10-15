using MethodCache.TestSupport;
using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class PolicyContractTests(SqlServerFixture sql)
{
    [SkippableFact]
    public async Task PolicyTransitionsAndDeadlines_ConformToSharedContract()
    {
        // Keep physical native TTLs ahead of wall-clock setup; expiry assertions use source deadlines on this clock.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var schema = "policy_" + Guid.NewGuid().ToString("N");
        var connection = sql.RequireConnectionString();
        async Task<ServiceProvider> Create(PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
            services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
            services.Configure<DistributedCacheOptions>(o => { o.OperationTimeout = TimeSpan.FromSeconds(5); o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
            services.AddMethodCacheSqlServer(o => { o.ConnectionString = connection; o.SchemaName = schema; o.BackplanePollingInterval = TimeSpan.FromMilliseconds(50); });
            var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().ToArray();
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await SqlServerFixture.WaitForTablesAsync(hosted, deadline.Token);
            return provider;
        }
        await PolicyBackendContract.RunAsync(Create, time);
    }
    [SkippableTheory]
    [InlineData("partial")]
    [InlineData("cancellation")]
    [InlineData("validation")]
    public async Task NativeInvalidationFaults_ConformToSharedContract(string scenario)
    {
        var options = new SqlServerOptions
        {
            ConnectionString = sql.RequireConnectionString(),
            SchemaName = "policy_fault_" + Guid.NewGuid().ToString("N")
        };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var store = new SqlServerDistributedCache(options);
            var backplane = new SqlServerBackplane(options);
            await using (backplane)
                await NativePolicyFaultContract.RunAsync(scenario, store, backplane);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

}
