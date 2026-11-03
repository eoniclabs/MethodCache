using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.SqlServer.Tests;

public class OwnedTransportOptionsTests
{
    [Fact]
    public void ConnectionFactory_IsResolvedOnceFromTheFinalContainer()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(new DatabaseSettings("Server=localhost;Database=Cache;Integrated Security=true"));
        services.AddMethodCache(mc => mc.UseSqlServer(options => options.ConnectionStringFactory = provider =>
        {
            calls++;
            return provider.GetRequiredService<DatabaseSettings>().ConnectionString;
        }));
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = "configured-last:");
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<SqlServerRuntimeOptions>();
        Assert.Equal("configured-last:", runtime.Prefix);
        Assert.Equal(new SqlConnectionStringBuilder(provider.GetRequiredService<DatabaseSettings>().ConnectionString) { Enlist = false }.ConnectionString,
            runtime.Options.ConnectionString);
        Assert.Same(runtime, provider.GetRequiredService<SqlServerRuntimeOptions>());
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.KeyPrefix = "too-late:");
    }

    [Theory]
    [InlineData("Server=localhost;Database=Cache;Integrated Security=true")]
    [InlineData("Server=localhost;Database=Cache;Integrated Security=true;Enlist=true")]
    public void Commands_NeverJoinTheApplicationsTransactionScope(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseSqlServer(connectionString));
        using var provider = services.BuildServiceProvider();

        var used = new SqlConnectionStringBuilder(provider.GetRequiredService<SqlServerRuntimeOptions>().Options.ConnectionString);

        Assert.False(used.Enlist);
        Assert.Equal("localhost", used.DataSource);
        Assert.Equal("Cache", used.InitialCatalog);
        Assert.True(used.IntegratedSecurity);
    }

    private sealed record DatabaseSettings(string ConnectionString);
}
