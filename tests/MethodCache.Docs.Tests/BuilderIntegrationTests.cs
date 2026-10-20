using MethodCache.Extensibility;
using MethodCache.Serialization.MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class BuilderIntegrationTests
{
    [Fact]
    public void StartupBuilder_IntegrationOrdering_LastSerializerWinsWithoutConnecting()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(b => b.UseRedis("localhost:6379")
            .UseMessagePack().UseJson().UseMessagePack(options => options.WithCompression(MessagePack.MessagePackCompression.None)));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<MessagePackCacheSerializer>(provider.GetRequiredKeyedService<ISerializer>("MethodCache"));
    }

    [Fact]
    public void StartupBuilder_ConfiguredRedisAndSqlServer_RegistersOwnedStorage()
    {
        var redis = new ServiceCollection();
        redis.AddMethodCache(b => b.UseRedis(o => { o.Configuration = "localhost:6379"; o.EnableBackplane = false; }));
        Assert.Contains(redis, descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(Microsoft.Extensions.Caching.Distributed.IDistributedCache));
        var sql = new ServiceCollection();
        sql.AddMethodCache(b => b.UseSqlServer("Server=localhost;Database=MethodCache;Integrated Security=True;TrustServerCertificate=True"));
        Assert.Contains(sql, descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(Microsoft.Extensions.Caching.Distributed.IDistributedCache));
        var configuredSql = new ServiceCollection();
        configuredSql.AddMethodCache(b => b.UseSqlServer(o =>
        {
            o.ConnectionString = "Server=localhost;Database=MethodCache;Integrated Security=True;TrustServerCertificate=True";
            o.EnableBackplane = false;
            o.CreateTablesIfMissing = false;
        }).UseJson(new System.Text.Json.JsonSerializerOptions()));
        using var provider = configuredSql.BuildServiceProvider();
        Assert.IsType<JsonCacheSerializer>(provider.GetRequiredKeyedService<ISerializer>("MethodCache"));
    }
}
