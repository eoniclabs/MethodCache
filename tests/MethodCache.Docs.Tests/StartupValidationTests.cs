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
    // Without the defaults, the machine's environment can't change what is tested: in Development the host also runs
    // ValidateOnBuild, which fails first on this assembly's sample services, and environment variables add settings
    private static HostApplicationBuilder NewHostBuilder() =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });

    [Fact]
    public async Task InvalidAppsettingsValue_FailsHostStart()
    {
        var builder = NewHostBuilder();
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
        var builder = NewHostBuilder();
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

        var policy = host.Services.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == "Sample.Type.Method").Options;
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

        Assert.Equal(TimeSpan.FromMinutes(10), provider.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == "Sample.Type.Method").Options.Duration);
    }
}

public interface IStartupProbe { [Cache] int Get(int id); }
public sealed class StartupProbe : IStartupProbe { public int Get(int id) => id; }
