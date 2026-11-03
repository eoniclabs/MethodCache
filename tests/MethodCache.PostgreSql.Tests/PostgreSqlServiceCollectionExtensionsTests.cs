using FluentAssertions;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MethodCache.PostgreSql.Tests;

public class PostgreSqlServiceCollectionExtensionsTests
{
    private const string ConnectionString = "Host=localhost;Database=cache;Username=app;Password=secret";

    [Fact]
    public void UsePostgreSql_RegistersCacheTablesAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UsePostgreSql(ConnectionString));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IDistributedCache>("MethodCache").Should().BeOfType<PostgreSqlDistributedCache>();
        provider.GetRequiredKeyedService<IBackplane>("MethodCache").Should().BeOfType<PostgreSqlBackplane>();
        provider.GetServices<IHostedService>().Should().ContainSingle(s => s is PostgreSqlTables);
    }

    [Fact]
    public void UsePostgreSql_Twice_RegistersOneTableInitializerAndOnePool()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UsePostgreSql(ConnectionString));
        services.AddMethodCache(mc => mc.UsePostgreSql(options => options.SchemaName = "cache"));
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(service => service is PostgreSqlTables);
        services.Should().ContainSingle(d => d.ServiceType == typeof(PostgreSqlConnection));
        provider.GetRequiredService<PostgreSqlRuntimeOptions>().Options.SchemaName.Should().Be("cache");
    }

    [Fact]
    public void UsePostgreSql_LeavesTheAppsDistributedCacheAlone()
    {
        var appCache = NSubstitute.Substitute.For<IDistributedCache>();
        var services = new ServiceCollection();
        services.AddSingleton(appCache);

        services.AddMethodCache(mc => mc.UsePostgreSql(ConnectionString));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().BeSameAs(appCache);
        services.Should().NotContain(d => d.ServiceType == typeof(NpgsqlDataSource));
    }

    [Fact]
    public void UsePostgreSql_CanSkipTablesAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UsePostgreSql(options =>
        {
            options.ConnectionString = ConnectionString;
            options.CreateTablesIfMissing = false;
            options.EnableBackplane = false;
        }));

        using var provider = services.BuildServiceProvider();
        provider.GetKeyedService<IBackplane>("MethodCache").Should().BeNull();
        // Still registered: it checks the tables the migration created, and deletes expired entries
        provider.GetServices<IHostedService>().Should().ContainSingle(service => service is PostgreSqlTables);
    }

    [Fact]
    public void UsePostgreSql_ConnectionFactory_IsResolvedOnceFromTheFinalContainer()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(new DatabaseSettings(ConnectionString));
        services.AddMethodCache(mc => mc.UsePostgreSql(options => options.ConnectionStringFactory = provider =>
        {
            calls++;
            return provider.GetRequiredService<DatabaseSettings>().ConnectionString;
        }));
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = "configured-last:");
        using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<PostgreSqlRuntimeOptions>();
        runtime.Prefix.Should().Be("configured-last:");
        runtime.Options.ConnectionString.Should().Be(ConnectionString);
        provider.GetRequiredService<PostgreSqlRuntimeOptions>().Should().BeSameAs(runtime);
        calls.Should().Be(1);
    }

    [Fact]
    public void UsePostgreSql_UnreadableConnectionString_FailsWhenTheCacheIsCreated()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UsePostgreSql("Host=localhost;NoSuchSetting=1"));
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<PostgreSqlRuntimeOptions>();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UsePostgreSql_ValidatesOptions()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UsePostgreSql(_ => { }));
        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<PostgreSqlOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData("connection", "PostgreSqlOptions.ConnectionString is required.")]
    [InlineData("tables", "PostgreSQL cache, schema-version, invalidations and cursor tables and their indexes need distinct names.")]
    [InlineData("retention", "PostgreSqlOptions.BackplaneRetention must be at least one minute.")]
    public void UsePostgreSql_InvalidOptions_FailWithTheMessageThatSaysWhatIsWrong(string problem, string message)
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UsePostgreSql(options => Break(options, problem)));
        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<PostgreSqlOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Failures.Should().Equal(message);
    }

    [Fact]
    public void UsePostgreSql_InvalidOptions_FailStartupWithTheMessageThatSaysWhatIsWrong()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UsePostgreSql(options => Break(options, "retention")));
        using var provider = services.BuildServiceProvider();
        var error = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.Should().Contain("PostgreSqlOptions.BackplaneRetention must be at least one minute.");
    }

    [Fact]
    public void UsePostgreSql_NullArguments_Throw()
    {
        var services = new ServiceCollection();

        services.Invoking(s => s.AddMethodCache(mc => mc.UsePostgreSql((string)null!))).Should().Throw<ArgumentNullException>();
        services.Invoking(s => s.AddMethodCache(mc => mc.UsePostgreSql(""))).Should().Throw<ArgumentException>();
        services.Invoking(s => s.AddMethodCache(mc => mc.UsePostgreSql((Action<PostgreSqlOptions>)null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UsePostgreSql_WithAnotherDistributedCache_IsRejected()
    {
        // A second provider would take over storage while PostgreSQL's backplane stayed
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UsePostgreSql(ConnectionString));

        var act = () => services.AddMethodCacheDistributedCache(_ => NSubstitute.Substitute.For<IDistributedCache>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*MethodCache.PostgreSql*UsePostgreSql*");
    }

    private static void Break(PostgreSqlOptions options, string problem)
    {
        options.ConnectionString = ConnectionString;
        switch (problem)
        {
            case "connection": options.ConnectionString = ""; break;
            case "tables": options.InvalidationsTableName = options.TableName; break;
            case "retention": options.BackplaneRetention = TimeSpan.FromSeconds(30); break;
        }
    }

    private sealed record DatabaseSettings(string ConnectionString);
}
