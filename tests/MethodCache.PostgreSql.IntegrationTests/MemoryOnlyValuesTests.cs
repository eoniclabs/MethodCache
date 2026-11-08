using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MethodCache.PostgreSql.IntegrationTests;

/// <summary>
/// With UsePostgreSql and StoreValues = false, values stay in each process's memory, from cached methods and manual
/// calls alike, and only the invalidations go through PostgreSQL (docs/features/distributed-cache.md, "Invalidations only").
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public class MemoryOnlyValuesTests(PostgreSqlFixture postgres)
{
    public interface IStock
    {
        [Cache(Duration = "1h", Tags = ["stock:{id}"])]
        Task<int> GetAsync(int id);
    }

    public sealed class Stock : IStock
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<int> GetAsync(int id) => Task.FromResult(Interlocked.Increment(ref _calls));
    }

    [SkippableFact]
    public async Task StoreValuesOff_ValuesStayInEachProcess_InvalidationsReachTheOthers()
    {
        var options = postgres.Options("memoryonly");
        try
        {
            var stockA = new Stock();
            var stockB = new Stock();
            await using var a = await StartAsync(stockA, options);
            await using var b = await StartAsync(stockB, options);
            var readerA = a.GetRequiredService<IStock>();
            var readerB = b.GetRequiredService<IStock>();

            await readerA.GetAsync(1);
            await readerB.GetAsync(1);
            await readerA.GetAsync(1);
            await readerB.GetAsync(1);
            // Each process ran its own method once: nothing was shared through PostgreSQL
            Assert.Equal((1, 1), (stockA.Calls, stockB.Calls));

            // A manual call's value stays in its process too
            await a.GetRequiredService<ICacheManager>().GetOrCreateAsync("manual", _ => ValueTask.FromResult("a"));
            Assert.Equal("b", await b.GetRequiredService<ICacheManager>().GetOrCreateAsync("manual", _ => ValueTask.FromResult("b")));

            var keys = a.GetRequiredService<IMethodCacheKeys>();
            await a.GetRequiredService<ICacheManager>().InvalidateTagAsync(keys.Tag<IStock>(s => s.GetAsync(1), "stock:{id}"));

            // The other process drops its copy when the invalidation arrives
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (stockB.Calls < 2)
            {
                await Task.Delay(50, deadline.Token);
                await readerB.GetAsync(1);
            }
            await readerA.GetAsync(1);
            Assert.Equal(2, stockA.Calls);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    private static async Task<ServiceProvider> StartAsync(Stock stock, PostgreSqlOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStock>(stock);
        services.AddMethodCache(mc => mc
            .UsePostgreSql(o =>
            {
                o.ConnectionString = options.ConnectionString;
                o.SchemaName = options.SchemaName;
                o.BackplanePollingInterval = TimeSpan.FromMilliseconds(50);
            })
            .Distributed(o => o.StoreValues = false));   // values stay in memory; invalidations reach every process
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await PostgreSqlFixture.WaitForTablesAsync(hosted, deadline.Token);
        return provider;
    }
}
