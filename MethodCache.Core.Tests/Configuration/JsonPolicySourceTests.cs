using System;
using System.Collections.Generic;
using MethodCache.Abstractions.Registry;
using MethodCache.Core;
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
            ["MethodCache:Services:Sample.Type.Method:RequireIdempotent"] = "true"
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
        Assert.True(result.Policy.RequireIdempotent);
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
}
