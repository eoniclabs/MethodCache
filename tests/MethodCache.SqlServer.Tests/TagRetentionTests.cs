using FluentAssertions;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MethodCache.SqlServer.Tests;

/// <summary>Tag retentions longer than SQL Server can store an expiry for.</summary>
public class TagRetentionTests
{
    private const string ConnectionString = "Server=localhost;Database=Cache;Integrated Security=true;TrustServerCertificate=true";

    [Fact]
    public void UseSqlServer_TagRetentionSqlServerCannotStore_IsRejectedAtStartup()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseSqlServer(ConnectionString));
        services.Configure<DistributedCacheOptions>(o => o.TagRetention = TimeSpan.MaxValue); // "keep forever"
        using var provider = services.BuildServiceProvider();

        // Either reject it like the other invalid TagRetention values, or clamp it so stamps can still be written
        var read = () => provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value;
        read.Should().Throw<OptionsValidationException>("every tag invalidation would otherwise fail and stay queued");
    }

    [Fact]
    public async Task WriteInvalidationStamp_MaximumRetention_IsClampedInsteadOfThrowing()
    {
        // What every tag invalidation does with that setting today: the stamp write throws before it connects.
        // (A clamped write goes on to connect, and fails with a SqlException here, which this test allows.)
        using var provider = new ServiceCollection().AddMethodCache(mc => mc.UseSqlServer(ConnectionString)).BuildServiceProvider();
        var store = (IAtomicInvalidationStore)provider.GetRequiredKeyedService<IDistributedCache>("MethodCache");

        var write = () => store.WriteInvalidationStampAsync("methodcache:t:T", 1, TimeSpan.MaxValue);

        await write.Should().NotThrowAsync<ArgumentOutOfRangeException>();
    }
}
