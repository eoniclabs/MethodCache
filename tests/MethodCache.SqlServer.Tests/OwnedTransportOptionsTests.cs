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
        Assert.Equal(provider.GetRequiredService<DatabaseSettings>().ConnectionString, runtime.Options.ConnectionString);
        Assert.Same(runtime, provider.GetRequiredService<SqlServerRuntimeOptions>());
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.KeyPrefix = "too-late:");
    }

    private sealed record DatabaseSettings(string ConnectionString);
}
