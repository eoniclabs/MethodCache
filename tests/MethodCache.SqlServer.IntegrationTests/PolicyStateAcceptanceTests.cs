using MethodCache.SqlServer;
using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class PolicyStateAcceptanceTests(SqlServerFixture sql)
{
    [SkippableTheory]
    [InlineData("lookup")]
    [InlineData("equivalent")]
    [InlineData("reload")]
    [InlineData("specificity")]
    [InlineData("disabled")]
    [InlineData("refresh")]
    [InlineData("custom")]
    [InlineData("sync")]
    [InlineData("unready-sync")]
    [InlineData("lost-message")]
    public async Task PolicyState_ConformsToSharedNativeContract(string scenario)
    {
        // Keep physical native TTLs ahead of wall-clock setup; expiry assertions use source deadlines on this clock.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var options = new SqlServerOptions { ConnectionString = sql.RequireConnectionString(), SchemaName = "policy_state_" + Guid.NewGuid().ToString("N") };
        async Task<ServiceProvider> Create(PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
            services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
            services.Configure<DistributedCacheOptions>(o => { o.OperationTimeout = TimeSpan.FromSeconds(5); o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
            services.AddMethodCache(mc => mc.UseSqlServer(o => { o.ConnectionString = options.ConnectionString; o.SchemaName = options.SchemaName; o.BackplanePollingInterval = TimeSpan.FromMilliseconds(50); }));
            if (scenario is "unready-sync" or "disabled" or "refresh") GatedNativePolicyStore.Decorate(services, holdReads: scenario == "unready-sync");
            PolicyMessageGate.Decorate(services);
            var provider = services.BuildServiceProvider();
            try
            {
                var hosted = provider.GetServices<IHostedService>().ToArray();
                foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await SqlServerFixture.WaitForTablesAsync(hosted, deadline.Token);
                return provider;
            }
            catch { await provider.DisposeAsync(); throw; }
        }
        try { await PolicyBackendContract.RunAcceptanceAsync(scenario, Create, time); }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }
}
