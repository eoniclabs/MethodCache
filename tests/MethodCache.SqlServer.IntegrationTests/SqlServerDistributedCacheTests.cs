using FluentAssertions;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddMethodCache();
        services.AddMethodCacheSqlServer(options =>
        {
            options.ConnectionString = connectionString;
            options.SchemaName = _schema;
            options.BackplanePollingInterval = TimeSpan.FromMilliseconds(100);
        });

        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

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

    private async Task<int> CountRowsAsync(string table)
    {
        await using var connection = new SqlConnection(_sqlServer.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM [{_schema}].[{table}]", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
