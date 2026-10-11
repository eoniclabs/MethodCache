using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MethodCache.SqlServer;

namespace MethodCache.SqlServer.IntegrationTests;

/// <summary>
/// Two service providers sharing one database stand in for two application instances. Each test uses its own
/// schema, so the tables are created from scratch every time.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerDistributedCacheTests : IAsyncLifetime
{
    private static readonly TimeSpan Eventually = TimeSpan.FromSeconds(15);

    private readonly SqlServerFixture _sqlServer;
    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _nodes = new();

    public SqlServerDistributedCacheTests(SqlServerFixture sqlServer)
    {
        _sqlServer = sqlServer;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }
    }

    private async Task<ICacheManager> CreateNodeAsync()
    {
        var connectionString = _sqlServer.RequireConnectionString();
        var services = new ServiceCollection();
        // SQL Server runs under emulation on some developer hosts, and all three TFMs share its CPU in CI.
        // Test sharing/invalidations with a realistic infrastructure budget; production still defaults to 1s.
        services.Configure<DistributedCacheOptions>(options => options.OperationTimeout = TimeSpan.FromSeconds(5));
        services.AddMethodCache();
        services.AddMethodCacheSqlServer(options =>
        {
            options.ConnectionString = connectionString;
            options.SchemaName = _schema;
            options.BackplanePollingInterval = TimeSpan.FromMilliseconds(100);
        });

        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
        using var readinessDeadline = new CancellationTokenSource(Eventually);
        await SqlServerFixture.WaitForTablesAsync(hostedServices, readinessDeadline.Token);

        return provider.GetRequiredService<ICacheManager>();
    }

    private static async Task<string> Get(ICacheManager node, string key, string value, params string[] tags) =>
        await node.GetOrCreateAsync(key, (_, _) => ValueTask.FromResult(value), options => options
            .WithDuration(TimeSpan.FromMinutes(5))
            .WithTags(tags));

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Eventually;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the change should reach the other instance");
            await Task.Delay(50);
        }
    }

    [SkippableFact]
    public async Task CreatesTheTables_AndTheSecondInstanceReadsTheValueTheFirstStored()
    {
        var first = await CreateNodeAsync();
        var second = await CreateNodeAsync();

        await Get(first, "key", "from first");

        (await Get(second, "key", "from second")).Should().Be("from first");
        (await CountRowsAsync("MethodCache")).Should().BeGreaterThan(0);
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
    public async Task ABurstOfInvalidations_DoesNotDelayNewOnes()
    {
        var first = await CreateNodeAsync();
        var second = await CreateNodeAsync();
        await Get(first, "key", "v1");
        (await Get(second, "key", "unused")).Should().Be("v1");

        // More rows than one read returns, all recent, from another instance
        await ExecuteAsync($"""
            INSERT INTO [{_schema}].[MethodCacheInvalidations] ([Scope], [InstanceId], [MessageType], [Target])
            SELECT TOP (3000) @scope, 'other', 0, CONCAT('unrelated', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
        await first.InvalidateByKeysAsync("key");

        var started = DateTime.UtcNow;
        await WaitUntil(async () => await Get(second, "key", "v2") == "v2");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [SkippableFact]
    public async Task AnInvalidationThatCommitsAfterManyLaterOnes_StillArrives()
    {
        var first = await CreateNodeAsync();
        var second = await CreateNodeAsync();
        await Get(first, "key", "v1");

        // Once the first write's own notice has passed, the second instance holds v1 in memory only
        await Task.Delay(500);
        (await Get(second, "key", "unused")).Should().Be("v1");
        await ExecuteAsync($"DELETE FROM [{_schema}].[MethodCache];");

        // A transaction takes 201 identity values, its invalidation first, and commits only after 500 later rows
        // were read
        await using var connection = new SqlConnection(_sqlServer.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using (var insert = new SqlCommand(
            $"""
            INSERT INTO [{_schema}].[MethodCacheInvalidations] ([Scope], [InstanceId], [MessageType], [Target]) VALUES (@scope, 'other', 0, 'key');
            INSERT INTO [{_schema}].[MethodCacheInvalidations] ([Scope], [InstanceId], [MessageType], [Target])
            SELECT TOP (200) @scope, 'other', 0, CONCAT('pending', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope("methodcache:"));
            await insert.ExecuteNonQueryAsync();
        }

        await ExecuteAsync($"""
            INSERT INTO [{_schema}].[MethodCacheInvalidations] ([Scope], [InstanceId], [MessageType], [Target])
            SELECT TOP (500) @scope, 'other', 0, CONCAT('unrelated', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
        await Task.Delay(500);
        (await Get(second, "key", "unused")).Should().Be("v1");

        await transaction.CommitAsync();

        await WaitUntil(async () => await Get(second, "key", "v2") == "v2");
    }

    [SkippableFact]
    public async Task Cleanup_RemovesEveryExpiredInvalidation()
    {
        await CreateNodeAsync();
        await ExecuteAsync($"""
            INSERT INTO [{_schema}].[MethodCacheInvalidations] ([Scope], [InstanceId], [MessageType], [Target], [CreatedAt])
            SELECT TOP (12000) @scope, 'other', 0, 'old', DATEADD(hour, -2, SYSUTCDATETIME())
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
        await CreateNodeAsync();

        await WaitUntil(async () => await CountRowsAsync("MethodCacheInvalidations") == 0);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_sqlServer.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope("methodcache:"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountRowsAsync(string table)
    {
        await using var connection = new SqlConnection(_sqlServer.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM [{_schema}].[{table}]", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
