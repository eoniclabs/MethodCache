using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// The docs promise that misconfiguration fails when the host starts, not on the first request.
/// </summary>
public class StartupValidationTests
{
    [Fact]
    public async Task GetOrCreateOnlySetting_OnCachedMethod_FailsHostStart()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IStartupProbe, StartupProbe>();
        builder.Services.AddMethodCache(c => c.ForService<IStartupProbe>()
            .Method(s => s.Get(Any<int>.Value))
            .Configure(o => o.OnHit(_ => { })));
        using var host = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        await host.StopAsync();
    }

    [Fact]
    public void GetOrCreateOnlySetting_WithoutAHost_FailsTheFirstCall()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStartupProbe, StartupProbe>();
        services.AddMethodCache(c => c.ForService<IStartupProbe>()
            .Method(s => s.Get(Any<int>.Value))
            .Configure(o => o.OnHit(_ => { })));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStartupProbe>());
    }

    [Fact]
    public async Task InvalidAppsettingsValue_FailsHostStart()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "soon"
        });
        builder.Services.AddMethodCache();
        builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);
        using var host = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task ConfigurationAddedAfterRegistration_AppliesFromTheStart()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "01:00:00"
        });
        builder.Services.AddMethodCache();
        builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);

        // A source added later, as Program.cs or WebApplicationFactory does
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "00:00:01"
        });
        using var host = builder.Build();
        await host.StartAsync();

        var policy = host.Services.GetRequiredService<IPolicyRegistry>().GetPolicy("Sample.Type.Method").Policy;
        Assert.Equal(TimeSpan.FromSeconds(1), policy.Duration);
        await host.StopAsync();
    }

    [Fact]
    public void InvalidValueOverriddenByALaterSource_IsNotAnError()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "soon"
        });
        var services = new ServiceCollection();
        services.AddMethodCacheFromConfiguration(configuration);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "00:10:00"
        });
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(10), provider.GetRequiredService<IPolicyRegistry>().GetPolicy("Sample.Type.Method").Policy.Duration);
    }
}

public interface IStartupProbe { [Cache] int Get(int id); }
public sealed class StartupProbe : IStartupProbe { public int Get(int id) => id; }
