using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Runtime;
using MethodCache.Core.Configuration.Sources;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class PolicySourcesTests
{
    [Fact]
    public async Task AttributePolicySource_LoadsAttributePolicies()
    {
        var source = new AttributePolicySource(typeof(PolicySourcesTests).Assembly);

        var snapshots = await source.GetSnapshotAsync();

        var snapshot = snapshots.Single(s => s.MethodId == "MethodCache.Core.Tests.Configuration.PolicySourcesTests+IAttributeTestService.GetValueAsync");
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Policy.Duration);
        Assert.Contains("users", snapshot.Policy.Tags);
        Assert.Equal(PolicySourceIds.Attributes, snapshot.SourceId);
    }

    [Fact]
    public async Task FluentPolicySource_UsesConfiguration()
    {
        var source = new FluentPolicySource(config =>
        {
            config.AddMethod("MethodCache.Core.Tests.Configuration.PolicySourcesTests+IFluentTestService.GetValue", new CacheMethodSettings
            {
                Duration = TimeSpan.FromSeconds(30),
                Tags = new List<string> { "fluent" },
                IsIdempotent = true,
                Version = 2
            });
        });

        var snapshots = await source.GetSnapshotAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.Policy.Duration);
        Assert.Equal(2, snapshot.Policy.Version);
        Assert.Contains("fluent", snapshot.Policy.Tags);
    }

    [Fact]
    public async Task ConfigFilePolicySource_LoadsEntries()
    {
        var data = new Dictionary<string, string?>
        {
            ["MethodCache:Services:PolicySourcesTests.IConfigService.GetConfig:Duration"] = "00:01:00",
            ["MethodCache:Services:PolicySourcesTests.IConfigService.GetConfig:Tags:0"] = "config"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(data!)
            .Build();

        var jsonSource = new JsonConfigurationSource(configuration);
        var policySource = new ConfigFilePolicySource(new[] { jsonSource });

        var snapshots = await policySource.GetSnapshotAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(TimeSpan.FromMinutes(1), snapshot.Policy.Duration);
        Assert.Contains("config", snapshot.Policy.Tags);
    }

    [Fact]
    public async Task RuntimeOverridePolicySource_EmitsChanges()
    {
        var overrideStore = new RuntimePolicyOverrideStore();
        var policySource = new RuntimeOverridePolicySource(overrideStore);
        var methodKey = "RuntimeOverride.Service.Get";

        overrideStore.ApplyOverrides(new[]
        {
            new MethodCacheConfigEntry
            {
                ServiceType = "RuntimeOverride.Service",
                MethodName = "Get",
                Settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(2) }
            }
        });

        var snapshots = await policySource.GetSnapshotAsync();
        Assert.Single(snapshots);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = policySource.WatchAsync(cts.Token).GetAsyncEnumerator();

        var updateTask = enumerator.MoveNextAsync().AsTask();
        overrideStore.ApplyOverrides(new[]
        {
            new MethodCacheConfigEntry
            {
                ServiceType = "RuntimeOverride.Service",
                MethodName = "Get",
                Settings = new CacheMethodSettings
                {
                    Duration = TimeSpan.FromMinutes(5),
                    Tags = new List<string> { "override" }
                }
            }
        });

        Assert.True(await updateTask);
        var updateChange = enumerator.Current;
        Assert.Equal(methodKey, updateChange.MethodId);
        Assert.Equal(PolicyChangeReason.Updated, updateChange.Reason);

        var removalTask = enumerator.MoveNextAsync().AsTask();
        Assert.True(overrideStore.RemoveOverride(methodKey));
        Assert.True(await removalTask);
        var removalChange = enumerator.Current;
        Assert.Equal(methodKey, removalChange.MethodId);
        Assert.Equal(PolicyChangeReason.Removed, removalChange.Reason);
    }

    private interface IAttributeTestService
    {
        [Cache(Duration = "00:05:00", Tags = new[] { "users" }, RequireIdempotent = true)]
        Task<string> GetValueAsync(int id);
    }

    private interface IFluentTestService
    {
        Task<string> GetValue(string id);
    }

    private interface IConfigService
    {
        Task<string> GetConfig(string key);
    }
}
