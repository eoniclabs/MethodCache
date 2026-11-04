using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// <see cref="IMethodCacheKeys.Tag{TService}"/> gives the tag a cached entry carries, so code that writes outside the
/// cached interface can invalidate it without building the tag text by hand.
/// </summary>
public class MethodCacheTagTests
{
    public enum Tier { Free = 1, Pro = 2 }

    public interface IPrices
    {
        const string RateTag = "price:{provider}:{model}";

        [Cache(Tags = ["prices", RateTag])]
        Task<decimal?> GetRateAsync(string provider, string model);

        [Cache(Tags = ["plan:{tenantId}:{tier}"])]
        Task<string> GetPlanAsync(Guid tenantId, Tier tier);
    }

    public sealed class Prices : IPrices
    {
        public int Calls;

        public Task<decimal?> GetRateAsync(string provider, string model)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<decimal?>(1.5m);
        }

        public Task<string> GetPlanAsync(Guid tenantId, Tier tier)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(tier.ToString());
        }
    }

    private static ServiceProvider Build(Prices prices, Action<MethodCacheBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPrices>(prices);
        services.AddMethodCache(builder =>
        {
            builder.FromAssemblyOf<MethodCacheTagTests>();
            configure?.Invoke(builder);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Tag_EscapedStringArgument_InvalidatesTheEntryAHandBuiltTagMisses()
    {
        var prices = new Prices();
        await using var provider = Build(prices);
        var service = provider.GetRequiredService<IPrices>();
        var cache = provider.GetRequiredService<ICacheManager>();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();

        await service.GetRateAsync("ollama", "gemma3:4b");

        // The hand-built text the second user report started with: the colon in the model name is escaped in the real tag
        await cache.InvalidateTagAsync("price:ollama:gemma3:4b");
        await service.GetRateAsync("ollama", "gemma3:4b");
        Assert.Equal(1, prices.Calls);

        var tag = keys.Tag<IPrices>(s => s.GetRateAsync("ollama", "gemma3:4b"), IPrices.RateTag);
        Assert.Equal(@"price:ollama:gemma3\:4b", tag);
        await cache.InvalidateTagAsync(tag);
        await service.GetRateAsync("ollama", "gemma3:4b");
        Assert.Equal(2, prices.Calls);
    }

    [Fact]
    public async Task Tag_GuidAndEnumArguments_MatchTheTagOnTheCachedEntry()
    {
        var prices = new Prices();
        await using var provider = Build(prices);
        var tenant = Guid.NewGuid();
        await provider.GetRequiredService<IPrices>().GetPlanAsync(tenant, Tier.Pro);
        var keys = provider.GetRequiredService<IMethodCacheKeys>();

        var tag = keys.Tag<IPrices>(s => s.GetPlanAsync(tenant, Tier.Pro), "plan:{tenantId}:{tier}");

        Assert.Equal($"plan:{tenant:N}:2", tag);
        var entry = provider.GetRequiredService<ICacheAdministrator>()
            .GetEntry(keys.For<IPrices>(s => s.GetPlanAsync(tenant, Tier.Pro)));
        Assert.NotNull(entry);
        Assert.Contains(tag, entry.Tags);
        Assert.Equal(1, prices.Calls);
    }

    [Fact]
    public async Task Tag_StaticTag_ReturnsItUnchangedWithoutRunningTheMethod()
    {
        var prices = new Prices();
        await using var provider = Build(prices);

        var tag = provider.GetRequiredService<IMethodCacheKeys>().Tag<IPrices>(s => s.GetRateAsync("a", "b"), "prices");

        Assert.Equal("prices", tag);
        Assert.Equal(0, prices.Calls);
    }

    [Fact]
    public async Task Tag_TemplateAddedInStartupCode_IsAcceptedAndFilledIn()
    {
        var prices = new Prices();
        await using var provider = Build(prices, builder => builder
            .Method((IPrices s) => s.GetRateAsync(Any<string>.Value, Any<string>.Value))
            .Configure(o => o.AddTags("provider:{provider}")));

        var tag = provider.GetRequiredService<IMethodCacheKeys>().Tag<IPrices>(s => s.GetRateAsync("ollama", "m"), "provider:{provider}");

        Assert.Equal("provider:ollama", tag);
    }

    [Fact]
    public async Task Tag_TemplateThatIsNotATagOfTheMethod_ThrowsAndListsItsTags()
    {
        await using var provider = Build(new Prices());
        var keys = provider.GetRequiredService<IMethodCacheKeys>();

        var error = Assert.Throws<InvalidOperationException>(
            () => keys.Tag<IPrices>(s => s.GetRateAsync("a", "b"), "price:{model}"));

        Assert.Contains("\"price:{model}\"", error.Message);
        Assert.Contains("\"price:{provider}:{model}\"", error.Message);
        Assert.Contains("\"prices\"", error.Message);
    }

    [Fact]
    public async Task Tag_KeyedRegistration_UsesTheKeyedPolicy()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IPrices, Prices>("eu");
        services.AddMethodCache(builder => builder.FromAssemblyOf<MethodCacheTagTests>());
        await using var provider = services.BuildServiceProvider();

        var tag = provider.GetRequiredService<IMethodCacheKeys>()
            .Tag<IPrices>(s => s.GetRateAsync("p", "m"), IPrices.RateTag, serviceKey: "eu");

        Assert.Equal("price:p:m", tag);
    }
}
