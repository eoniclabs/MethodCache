using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Abstractions.Registry;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public sealed class JsonPolicySourceTests
{
    [Fact]
    public void AddMethodCacheFromConfiguration_RegistersJsonPolicies()
    {
        var data = new Dictionary<string, string?>
        {
            ["MethodCache:Defaults:Duration"] = "00:00:30",
            ["MethodCache:Defaults:Tags:0"] = "default",
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "00:05:00",
            ["MethodCache:Services:Sample.Type.Method:Tags:0"] = "json",
            ["MethodCache:Services:Sample.Type.Method:SlidingExpiration"] = "00:01:00",
            ["MethodCache:Services:Sample.Type.Method:RefreshAhead"] = "00:00:10"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();

        var services = new ServiceCollection();
        services.AddMethodCacheFromConfiguration(configuration);

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPolicyRegistry>();

        var result = registry.GetPolicy("Sample.Type.Method");
        Assert.Equal(TimeSpan.FromMinutes(5), result.Policy.Duration);
        Assert.Contains("json", result.Policy.Tags);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Policy.SlidingExpiration);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Policy.RefreshAhead);
    }

    [Fact]
    public void FailSafeSection_FillsWhatAMethodLeavesOpen_AndAMethodCanTurnItOff()
    {
        var data = new Dictionary<string, string?>
        {
            ["MethodCache:FailSafe:Duration"] = "01:00:00",
            ["MethodCache:FailSafe:Timeout"] = "00:00:00.200",
            ["MethodCache:FailSafe:RetryAfter"] = "00:00:10",
            ["MethodCache:Services:Sample.Type.Balance:FailSafe"] = "off",
            ["MethodCache:Services:Sample.Type.Rates:FailSafe"] = "00:10:00",
            ["MethodCache:Services:Sample.Type.Rates:FailSafeTimeout"] = "00:00:01"
        };

        var services = new ServiceCollection();
        services.AddMethodCacheFromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(data).Build());
        var registry = services.BuildServiceProvider().GetRequiredService<IPolicyRegistry>();

        // Methods without settings of their own get the section through the defaults; the source generator
        // integration tests cover that end to end.
        Assert.Equal(TimeSpan.Zero, registry.GetPolicy("Sample.Type.Balance").Policy.FailSafe);

        var rates = registry.GetPolicy("Sample.Type.Rates").Policy;
        Assert.Equal(TimeSpan.FromMinutes(10), rates.FailSafe);
        Assert.Equal(TimeSpan.FromSeconds(1), rates.FailSafeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), rates.FailSafeRetryAfter);
    }

    [Fact]
    public void FailSafeSection_WithoutDuration_Throws()
    {
        var data = new Dictionary<string, string?> { ["MethodCache:FailSafe:Timeout"] = "00:00:01" };
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddMethodCacheFromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(data).Build());
            services.BuildServiceProvider().GetRequiredService<IPolicyRegistry>().GetPolicy("Sample.Type.Other");
        });
        Assert.Contains("MethodCache:FailSafe", error.Message);
    }

    [Fact]
    public void AddMethodCacheFromConfiguration_PropagatesMetadata()
    {
        var data = new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Metadata:group"] = "config",
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "00:01:00"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();

        var services = new ServiceCollection();
        services.AddMethodCacheFromConfiguration(configuration);

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        var result = registry.GetPolicy("Sample.Type.Method");

        Assert.Equal("config", result.Policy.Metadata["group"]);
    }

    [Fact]
    public async Task ReloadedConfiguration_UpdatesAddsAndRemovesPolicies_WithoutRestart()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "00:01:00",
            ["MethodCache:Services:Sample.Type.Other:Duration"] = "00:02:00"
        });
        await using var provider = BuildProvider(configuration);
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy("Sample.Type.Method").Policy.Duration);

        configuration["MethodCache:Services:Sample.Type.Method:Duration"] = "00:10:00";
        configuration["MethodCache:Services:Sample.Type.Method:SlidingExpiration"] = "00:00:30";
        configuration["MethodCache:Services:Sample.Type.Added:Duration"] = "00:03:00";
        RemoveSection(configuration, "MethodCache:Services:Sample.Type.Other");
        configuration.Reload();

        await WaitUntilAsync(() => registry.GetPolicy("Sample.Type.Method").Policy.Duration == TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromSeconds(30), registry.GetPolicy("Sample.Type.Method").Policy.SlidingExpiration);
        await WaitUntilAsync(() => registry.GetPolicy("Sample.Type.Added").Policy.Duration == TimeSpan.FromMinutes(3));
        await WaitUntilAsync(() => registry.GetPolicy("Sample.Type.Other").Policy.Duration == null);
    }

    [Fact]
    public async Task ReloadedConfiguration_KeepsDiagnosticsAttribution_AndPicksUpNotes()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "00:01:00",
            ["MethodCache:Services:Sample.Type.Method:Notes"] = "first"
        });
        await using var provider = BuildProvider(configuration);
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        Assert.Equal("first", Assert.Single(registry.GetPolicy("Sample.Type.Method").Contributions).Notes);

        configuration["MethodCache:Services:Sample.Type.Method:Notes"] = "second";
        configuration.Reload();

        await WaitUntilAsync(() => registry.GetPolicy("Sample.Type.Method").Contributions.Any(c => c.Notes == "second"));
        var contribution = Assert.Single(registry.GetPolicy("Sample.Type.Method").Contributions);
        Assert.Equal(PolicySourceIds.ConfigurationFiles, contribution.SourceId);
    }

    [Fact]
    public async Task ReloadedConfiguration_WithInvalidValue_KeepsPreviousPolicy()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["MethodCache:Services:Sample.Type.Method:Duration"] = "00:01:00"
        });
        await using var provider = BuildProvider(configuration);
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy("Sample.Type.Method").Policy.Duration);

        configuration["MethodCache:Services:Sample.Type.Method:Duration"] = "five minutes";
        configuration.Reload();
        configuration["MethodCache:Services:Sample.Type.Method:Duration"] = "00:01:00";
        configuration["MethodCache:Services:Sample.Type.Method:Version"] = "2";
        configuration.Reload();

        // The valid reload after the invalid one still applies
        await WaitUntilAsync(() => registry.GetPolicy("Sample.Type.Method").Policy.Version == 2);
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy("Sample.Type.Method").Policy.Duration);
    }

    [Theory]
    [InlineData("Duration", "five minutes")]
    [InlineData("Duration", "-00:01:00")]
    [InlineData("SlidingExpiration", "00:00:00")]
    [InlineData("RefreshAhead", "soon")]
    [InlineData("Version", "-1")]
    public void AddMethodCacheFromConfiguration_Throws_ForInvalidValues(string key, string value)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"MethodCache:Services:Sample.Type.Method:{key}"] = value
        });

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMethodCacheFromConfiguration(configuration));

        Assert.Contains($"MethodCache:Services:Sample.Type.Method:{key}", exception.Message);
    }

    private static IConfigurationRoot BuildConfiguration(Dictionary<string, string?> data)
        => new ConfigurationBuilder().AddInMemoryCollection(data).Build();

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddMethodCacheFromConfiguration(configuration);
        return services.BuildServiceProvider();
    }

    private static void RemoveSection(IConfiguration configuration, string path)
    {
        foreach (var child in configuration.GetSection(path).AsEnumerable(makePathsRelative: false).ToList())
        {
            configuration[child.Key] = null;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The reloaded configuration was not applied.");
            await Task.Delay(10);
        }
    }
}
