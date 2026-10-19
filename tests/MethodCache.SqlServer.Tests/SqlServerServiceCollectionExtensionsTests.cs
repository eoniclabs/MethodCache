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
    public void UseSqlServer_RegistersCacheTablesAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UseSqlServer(ConnectionString));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IDistributedCache>("MethodCache").Should().BeOfType<SqlServerDistributedCache>();
        provider.GetRequiredKeyedService<IBackplane>("MethodCache").Should().BeOfType<SqlServerBackplane>();
        provider.GetServices<IHostedService>().Should().ContainSingle(s => s is SqlServerTables);
    }

    [Fact]
    public void UseSqlServer_LeavesTheAppsDistributedCacheAlone()
    {
        var appCache = NSubstitute.Substitute.For<IDistributedCache>();
        var services = new ServiceCollection();
        services.AddSingleton(appCache);

        services.AddMethodCache(mc => mc.UseSqlServer(ConnectionString));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().BeSameAs(appCache);
        services.Should().NotContain(d => d.ServiceType == typeof(IConfigureOptions<SqlServerCacheOptions>));
    }

    [Fact]
    public void UseSqlServer_PassesTableSettingsToTheCache()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UseSqlServer(options =>
        {
            options.ConnectionString = ConnectionString;
            options.SchemaName = "cache";
            options.TableName = "Entries";
        }));
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = "app:");
        using var provider = services.BuildServiceProvider();

        var cacheOptions = MethodCacheSqlServerServiceCollectionExtensions.CacheOptions(provider.GetRequiredService<IOptions<SqlServerOptions>>().Value);
        cacheOptions.ConnectionString.Should().Be(ConnectionString);
        cacheOptions.SchemaName.Should().Be("cache");
        cacheOptions.TableName.Should().Be("Entries");
        provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.KeyPrefix.Should().Be("app:");
    }

    [Fact]
    public void UseSqlServer_CanSkipTablesAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UseSqlServer(options =>
        {
            options.ConnectionString = ConnectionString;
            options.CreateTablesIfMissing = false;
            options.EnableBackplane = false;
        }));

        using var provider = services.BuildServiceProvider();
        provider.GetKeyedService<IBackplane>("MethodCache").Should().BeNull();
        provider.GetServices<IHostedService>().Should().ContainSingle(service => service is SqlServerTables);
    }

    [Fact]
    public void UseSqlServer_ValidatesOptions()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UseSqlServer(_ => { }));
        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<SqlServerOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
    }
}
