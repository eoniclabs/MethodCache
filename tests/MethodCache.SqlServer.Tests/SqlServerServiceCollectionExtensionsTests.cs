using FluentAssertions;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MethodCache.SqlServer.Tests;

public class SqlServerServiceCollectionExtensionsTests
{
    private const string ConnectionString = "Server=localhost;Database=Cache;Integrated Security=true;TrustServerCertificate=true";

    [Fact]
    public void AddMethodCacheSqlServer_RegistersCacheTablesAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCacheSqlServer(ConnectionString);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IDistributedCache>("MethodCache").Should().BeOfType<SqlServerCache>();
        provider.GetRequiredService<IBackplane>().Should().BeOfType<SqlServerBackplane>();
        provider.GetServices<IHostedService>().Should().ContainSingle(s => s is SqlServerTables);
    }

    [Fact]
    public void AddMethodCacheSqlServer_LeavesTheAppsDistributedCacheAlone()
    {
        var appCache = NSubstitute.Substitute.For<IDistributedCache>();
        var services = new ServiceCollection();
        services.AddSingleton(appCache);

        services.AddMethodCacheSqlServer(ConnectionString);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().BeSameAs(appCache);
        services.Should().NotContain(d => d.ServiceType == typeof(IConfigureOptions<SqlServerCacheOptions>));
    }

    [Fact]
    public void AddMethodCacheSqlServer_PassesTableSettingsToTheCache()
    {
        var services = new ServiceCollection();

        SqlServerOptions? captured = null;
        services.AddMethodCacheSqlServer(options =>
        {
            captured = options;
            options.ConnectionString = ConnectionString;
            options.SchemaName = "cache";
            options.TableName = "Entries";
            options.KeyPrefix = "app:";
        });
        using var provider = services.BuildServiceProvider();

        var cacheOptions = MethodCacheSqlServerServiceCollectionExtensions.CacheOptions(captured!);
        cacheOptions.ConnectionString.Should().Be(ConnectionString);
        cacheOptions.SchemaName.Should().Be("cache");
        cacheOptions.TableName.Should().Be("Entries");
        provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.KeyPrefix.Should().Be("app:");
    }

    [Fact]
    public void AddMethodCacheSqlServer_CanSkipTablesAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCacheSqlServer(options =>
        {
            options.ConnectionString = ConnectionString;
            options.CreateTablesIfMissing = false;
            options.EnableBackplane = false;
        });

        services.Should().NotContain(d => d.ServiceType == typeof(IBackplane));
        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddMethodCacheSqlServer_ValidatesOptions()
    {
        var services = new ServiceCollection();

        var act = () => services.AddMethodCacheSqlServer(_ => { });

        act.Should().Throw<InvalidOperationException>();
    }
}
