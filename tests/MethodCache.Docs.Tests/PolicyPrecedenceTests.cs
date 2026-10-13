using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// How a method's sources combine (docs/features/configuration.md): tags from overrides are added to the
/// <c>[Cache]</c> attribute's, so <c>[CacheInvalidate]</c> keeps working, and defaults, also runtime ones, only fill
/// in what the method does not set.
/// </summary>
public class PolicyPrecedenceTests
{
    private static readonly string GetId = typeof(IPrecedenceOrders).FullName!.Replace('+', '.') + ".GetAsync";

    [Fact]
    public async Task RuntimeTagOverride_KeepsTheAttributeTags_SoCacheInvalidateStillReachesTheEntry()
    {
        await using var provider = Build();
        var orders = provider.GetRequiredService<IPrecedenceOrders>();
        await provider.GetRequiredService<IRuntimeCacheConfigurator>()
            .UpsertAsync<IPrecedenceOrders, Task<int>>(s => s.GetAsync(Any<int>.Value), o => o.WithTags("incident"));

        await orders.GetAsync(1);
        await orders.UpdateAsync(1);
        await orders.GetAsync(1);

        Assert.Equal(new[] { "order:{id}", "incident" }, provider.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId).Policy.Tags);
        Assert.Equal(2, provider.GetRequiredService<PrecedenceOrders>().Calls);
    }

    [Fact]
    public async Task RuntimeOverrideWithClearTags_ReplacesTheAttributeTags()
    {
        await using var provider = Build();
        var orders = provider.GetRequiredService<IPrecedenceOrders>();
        await provider.GetRequiredService<IRuntimeCacheConfigurator>()
            .UpsertAsync<IPrecedenceOrders, Task<int>>(s => s.GetAsync(Any<int>.Value), o => o.ClearTags().WithTags("incident"));

        await orders.GetAsync(1);
        await orders.UpdateAsync(1);
        await orders.GetAsync(1);

        Assert.Equal(new[] { "incident" }, provider.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId).Policy.Tags);
        Assert.Equal(1, provider.GetRequiredService<PrecedenceOrders>().Calls);
    }

    [Fact]
    public async Task AppsettingsTags_AreAddedToTheAttributeTags_UnlessClearTagsIsTrue()
    {
        await using var added = Build(new() { [$"MethodCache:Services:{GetId}:Tags:0"] = "orders" });
        await using var cleared = Build(new()
        {
            [$"MethodCache:Services:{GetId}:Tags:0"] = "orders",
            [$"MethodCache:Services:{GetId}:ClearTags"] = "true",
        });

        Assert.Equal(new[] { "order:{id}", "orders" }, added.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId).Policy.Tags);
        Assert.Equal(new[] { "orders" }, cleared.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId).Policy.Tags);
    }

    [Fact]
    public async Task AppsettingsClearTags_ThatIsNotABoolean_IsRejected()
    {
        await using var provider = Build(new() { [$"MethodCache:Services:{GetId}:ClearTags"] = "yes" });

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId));
        Assert.Contains("true or false", error.Message);
    }

    [Fact]
    public async Task RuntimeDefaultPolicy_OnlyFillsInWhatTheAttributeDoesNotSet()
    {
        await using var provider = Build();
        provider.GetRequiredService<IPrecedenceOrders>();
        var registry = provider.GetRequiredService<IPolicyRegistry>();

        await provider.GetRequiredService<IRuntimeCacheConfigurator>().ApplyAsync(c => c.DefaultPolicy(o => o
            .WithDuration(TimeSpan.FromSeconds(30))
            .WithSlidingExpiration(TimeSpan.FromSeconds(10))));

        var policy = registry.GetPolicy(GetId).Policy;
        Assert.Equal(TimeSpan.FromHours(1), policy.Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.SlidingExpiration);
    }

    private static ServiceProvider Build(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<PrecedenceOrders>();
        services.AddSingleton<IPrecedenceOrders>(sp => sp.GetRequiredService<PrecedenceOrders>());
        services.AddMethodCache();
        if (settings != null)
        {
            services.AddMethodCacheFromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        }

        return services.BuildServiceProvider();
    }
}

public interface IPrecedenceOrders
{
    [Cache(Duration = "01:00:00", Tags = new[] { "order:{id}" })]
    Task<int> GetAsync(int id);

    [CacheInvalidate(Tags = new[] { "order:{id}" })]
    Task UpdateAsync(int id);
}

public sealed class PrecedenceOrders : IPrecedenceOrders
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<int> GetAsync(int id)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(id);
    }

    public Task UpdateAsync(int id) => Task.CompletedTask;
}
