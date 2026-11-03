using FluentAssertions;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.PostgreSql.IntegrationTests;

/// <summary>
/// Two service providers sharing one database stand in for two application instances. Each test uses its own
/// schema, so the tables are created from scratch every time.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlDistributedCacheTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Eventually = TimeSpan.FromSeconds(15);

    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _nodes = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            foreach (var hosted in node.GetServices<IHostedService>()) await hosted.StopAsync(CancellationToken.None);
            await node.DisposeAsync();
        }

        if (postgres.ConnectionString != null)
            await PostgreSqlFixture.DropSchemaAsync(new PostgreSqlOptions { ConnectionString = postgres.ConnectionString, SchemaName = _schema });
    }

    private async Task<ICacheManager> CreateNodeAsync(TimeProvider? time = null)
    {
        var connectionString = postgres.RequireConnectionString();
        var services = new ServiceCollection();
        if (time != null) services.AddSingleton<TimeProvider>(time);
        services.Configure<DistributedCacheOptions>(options => options.OperationTimeout = TimeSpan.FromSeconds(5));
        services.AddMethodCache();
        services.AddMethodCache(mc => mc.UsePostgreSql(options =>
        {
            options.ConnectionString = connectionString;
            options.SchemaName = _schema;
            options.BackplanePollingInterval = TimeSpan.FromMilliseconds(100);
        }));

        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices) await hostedService.StartAsync(CancellationToken.None);
        using var readinessDeadline = new CancellationTokenSource(Eventually);
        await PostgreSqlFixture.WaitForTablesAsync(hostedServices, readinessDeadline.Token);
        return provider.GetRequiredService<ICacheManager>();
    }

    private static async Task<string> Get(ICacheManager node, string key, string value, params string[] tags) =>
        await node.GetOrCreateAsync(key, _ => ValueTask.FromResult(value), options => options
            .WithDuration(TimeSpan.FromMinutes(5))
            .AddTags(tags));

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Eventually;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the change should reach the other instance");
            await Task.Delay(20);
        }
    }

    [SkippableFact]
    public async Task CreatesTheTables_AndTheSecondInstanceReadsTheValueTheFirstStored()
    {
        var first = await CreateNodeAsync();
        var second = await CreateNodeAsync();

        await Get(first, "key", "from first");

        (await Get(second, "key", "from second")).Should().Be("from first");
        (await CountRowsAsync("methodcache")).Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task TagInvalidation_ReachesTheOtherInstancesMemory()
    {
        var first = await CreateNodeAsync();
        var second = await CreateNodeAsync();
        await Get(first, "key", "v1", "users");
        (await Get(second, "key", "unused", "users")).Should().Be("v1");

        await first.InvalidateByTagsAsync("users");

        await WaitUntil(async () => await Get(second, "key", "v2", "users") == "v2");
    }

    [SkippableFact]
    public async Task KeyInvalidation_ReachesTheOtherInstancesMemory()
    {
        var first = await CreateNodeAsync();
        var second = await CreateNodeAsync();
        await Get(first, "key", "v1");
        (await Get(second, "key", "unused")).Should().Be("v1");

        await first.InvalidateByKeysAsync("key");

        await WaitUntil(async () => await Get(second, "key", "v2") == "v2");
    }

    [SkippableFact]
    public async Task Invalidation_ArrivesByNotification_WithoutWaitingForAPoll()
    {
        var first = await CreateNodeAsync();
        // On a clock that never moves, the second instance polls only when its doorbell rings
        var second = await CreateNodeAsync(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var backplane = (PostgreSqlBackplane)_nodes[^1].GetRequiredKeyedService<IBackplane>("MethodCache");
        await BackplaneTests.WaitUntilAsync(() => backplane.Listening);
        await Get(first, "key", "v1");
        (await Get(second, "key", "unused")).Should().Be("v1");
        await Get(first, "warm-up", "w1");
        (await Get(second, "warm-up", "unused")).Should().Be("w1");

        // Once one invalidation has arrived, the poll that connecting LISTEN rang for has run: the next needs a notification
        await first.InvalidateByKeysAsync("warm-up");
        await WaitUntil(async () => await Get(second, "warm-up", "w2") == "w2");
        await first.InvalidateByKeysAsync("key");

        await WaitUntil(async () => await Get(second, "key", "v2") == "v2");
    }

    [SkippableFact]
    public async Task LongKeyAndTag_AreSharedAndInvalidated()
    {
        var first = await CreateNodeAsync();
        var second = await CreateNodeAsync();
        var longKey = new string('k', 5000);
        var longTag = new string('t', 5000);

        await Get(first, longKey, "v1", longTag);
        (await Get(second, longKey, "unused", longTag)).Should().Be("v1", "a long key is stored under its hash");

        await first.InvalidateByTagsAsync(longTag);
        await WaitUntil(async () => await Get(second, longKey, "v2", longTag) == "v2");

        // Neither node fell back to memory only: a new value still reaches the other node through PostgreSQL
        await Get(first, "other", "from first");
        (await Get(second, "other", "from second")).Should().Be("from first");
    }

    private async Task<long> CountRowsAsync(string table) => await PostgreSqlFixture.ScalarAsync<long>(postgres.ConnectionString!,
        $"SELECT count(*) FROM {PostgreSqlOptions.Quote(_schema)}.{PostgreSqlOptions.Quote(table)}");
}
