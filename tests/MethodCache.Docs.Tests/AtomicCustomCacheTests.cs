using MethodCache.TestSupport;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class AtomicCustomCacheTests
{
    [Fact]
    public void CustomResultCacheWithoutAtomicStampContract_IsRejectedWhenManagerIsResolved()
    {
        using var provider = new ServiceCollection().AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddMethodCache().AddMethodCacheDistributedCache(_ => new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())))
            .BuildServiceProvider();

        var error = Assert.Throws<ArgumentException>(() => provider.GetRequiredService<ICacheManager>());

        Assert.Contains("IAtomicInvalidationStore", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationCacheWithoutAtomicStampContract_FailsWhenHostStarts()
    {
        using var host = BuildHost(services => services
            .AddDistributedMemoryCache()
            .AddMethodCacheDistributedCache());

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("IAtomicInvalidationStore", error.Message, StringComparison.Ordinal);
        Assert.Contains("owned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnedCacheWithoutAtomicStampContract_FailsWhenHostStarts()
    {
        using var host = BuildHost(services => services.AddMethodCacheDistributedCache(_ => new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()))));

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("IAtomicInvalidationStore", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDistributedCache_FailsWhenHostStarts()
    {
        using var host = BuildHost(services => services.AddMethodCacheDistributedCache());

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("IDistributedCache", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> AtomicRegistrations => new() { "redis", "sqlserver", "custom-owned", "custom-app" };

    [Theory]
    [MemberData(nameof(AtomicRegistrations))]
    public async Task AtomicDistributedCache_StartsHost(string registration)
    {
        using var host = BuildHost(services =>
        {
            switch (registration)
            {
                case "redis":
                    services.AddMethodCacheRedis("localhost:1");
                    break;
                case "sqlserver":
                    services.AddMethodCacheSqlServer(options =>
                    {
                        options.ConnectionString = "Server=localhost,1;Database=cache;User Id=sa;Password=x;Connect Timeout=1";
                        options.CreateTablesIfMissing = false;
                        options.EnableBackplane = false;
                    });
                    break;
                case "custom-owned":
                    services.AddMethodCacheDistributedCache(_ => new AtomicTestCache());
                    break;
                default:
                    services.AddSingleton<IDistributedCache, AtomicTestCache>().AddMethodCacheDistributedCache(sp => sp.GetRequiredService<IDistributedCache>());
                    break;
            }
        });

        var starting = host.StartAsync();
        if (registration == "sqlserver")
            ((FakeTimeProvider)host.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(3));
        await starting;
        await host.StopAsync();
    }

    private static IHost BuildHost(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider()).AddMethodCache();
        configure(builder.Services);
        return builder.Build();
    }
}
