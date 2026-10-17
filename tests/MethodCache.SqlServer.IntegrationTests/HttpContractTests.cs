using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class HttpContractTests(SqlServerFixture sql)
{
    [SkippableFact]
    public async Task HttpEpochs_WithDroppedBackplane_NoOpAndRollingFormats_ConformToContract()
    {
        var connection = sql.RequireConnectionString(); var schema = "http_contract_" + Guid.NewGuid().ToString("N");
        async Task<ServiceProvider> Create(HttpNativeContract.Origin origin, byte format, bool http)
        {
            var services = new ServiceCollection();
            services.AddMethodCacheSqlServer(o => { o.ConnectionString = connection; o.SchemaName = schema; });
            services.Configure<DistributedCacheOptions>(o => { o.ClockSkewTolerance = TimeSpan.Zero; o.OperationTimeout = TimeSpan.FromSeconds(5); });
            HttpNativeContract.Configure(services, origin, format, http);
            var provider = services.BuildServiceProvider(); var hosted = provider.GetServices<IHostedService>().ToArray();
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await SqlServerFixture.WaitForTablesAsync(hosted, deadline.Token);
            return provider;
        }
        await HttpNativeContract.RunAsync(Create);
    }
}
