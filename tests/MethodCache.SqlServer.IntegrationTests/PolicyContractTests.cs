using MethodCache.TestSupport;
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
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
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
}
