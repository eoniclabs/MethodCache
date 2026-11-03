using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.PostgreSql.IntegrationTests;

/// <summary>The contracts every distributed cache MethodCache ships must meet, run against PostgreSQL.</summary>
[Collection(PostgreSqlCollection.Name)]
public class ContractTests(PostgreSqlFixture postgres)
{
    [SkippableFact]
    public async Task HttpEpochs_WithDroppedBackplane_NoOpAndRollingFormats_ConformToContract()
    {
        var options = postgres.Options("http_contract");
        try
        {
            async Task<ServiceProvider> Create(HttpNativeContract.Origin origin, byte format, bool http)
            {
                var services = new ServiceCollection();
                services.AddMethodCache(mc => mc.UsePostgreSql(o => { o.ConnectionString = options.ConnectionString; o.SchemaName = options.SchemaName; }));
                services.Configure<DistributedCacheOptions>(o => { o.ClockSkewTolerance = TimeSpan.Zero; o.OperationTimeout = TimeSpan.FromSeconds(5); });
                HttpNativeContract.Configure(services, origin, format, http);
                return await StartAsync(services);
            }

            await HttpNativeContract.RunAsync(Create);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task PolicyTransitionsAndDeadlines_ConformToSharedContract()
    {
        // Keep physical native TTLs ahead of wall-clock setup; expiry assertions use source deadlines on this clock.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var options = postgres.Options("policy");
        try
        {
            async Task<ServiceProvider> Create(PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
            {
                var services = new ServiceCollection();
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
                services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
                services.Configure<DistributedCacheOptions>(o => { o.OperationTimeout = TimeSpan.FromSeconds(5); o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
                services.AddMethodCache(mc => mc.UsePostgreSql(o => { o.ConnectionString = options.ConnectionString; o.SchemaName = options.SchemaName; o.BackplanePollingInterval = TimeSpan.FromMilliseconds(50); }));
                PolicySubscriptionBarrier.Decorate(services);
                var provider = await StartAsync(services);
                await PolicySubscriptionBarrier.WaitForNodeAsync(provider);
                return provider;
            }

            await PolicyBackendContract.RunAsync(Create, time);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

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
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var options = postgres.Options("policy_state");
        async Task<ServiceProvider> Create(PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
            services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
            services.Configure<DistributedCacheOptions>(o => { o.OperationTimeout = TimeSpan.FromSeconds(5); o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
            services.AddMethodCache(mc => mc.UsePostgreSql(o => { o.ConnectionString = options.ConnectionString; o.SchemaName = options.SchemaName; o.BackplanePollingInterval = TimeSpan.FromMilliseconds(50); }));
            if (scenario is "unready-sync" or "disabled" or "refresh") GatedNativePolicyStore.Decorate(services, holdReads: scenario == "unready-sync");
            PolicyMessageGate.Decorate(services);
            return await StartAsync(services);
        }

        try { await PolicyBackendContract.RunAcceptanceAsync(scenario, Create, time); }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableTheory]
    [InlineData("partial")]
    [InlineData("cancellation")]
    [InlineData("validation")]
    public async Task NativeInvalidationFaults_ConformToSharedContract(string scenario)
    {
        var options = postgres.Options("policy_fault");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var store = new PostgreSqlDistributedCache(connection, options);
            await using var backplane = new PostgreSqlBackplane(connection, options);
            await NativePolicyFaultContract.RunAsync(scenario, store, backplane);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    private static async Task<ServiceProvider> StartAsync(ServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        try
        {
            var hosted = provider.GetServices<IHostedService>().ToArray();
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await PostgreSqlFixture.WaitForTablesAsync(hosted, deadline.Token);
            return provider;
        }
        catch { await provider.DisposeAsync(); throw; }
    }
}
