using MethodCache.Core.PolicyPipeline.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// Runtime overrides for a whole group or for every cached method: each method gets the same override of its own, a
/// mistake for one method changes none of them, and an override that can't work is refused instead of stored.
/// </summary>
public class RuntimeGroupOverrideTests
{
    private static readonly string PriceId = MethodIds.For(typeof(IShop), nameof(IShop.GetPriceAsync));
    private static readonly string DiscountId = MethodIds.For(typeof(IShop), nameof(IShop.GetDiscountAsync));
    private static readonly string StockId = MethodIds.For(typeof(IShop), nameof(IShop.GetStockAsync));

    public interface IShop
    {
        [Cache(Duration = "10m", Group = "pricing")] ValueTask<int> GetPriceAsync(int productId);
        [Cache(Duration = "10m", Group = "pricing")] ValueTask<int> GetDiscountAsync(int customerId);
        [Cache(Duration = "10m")] ValueTask<int> GetStockAsync(int productId);
    }

    public sealed class Shop : IShop
    {
        public int Loads;
        public ValueTask<int> GetPriceAsync(int productId) => ValueTask.FromResult(Interlocked.Increment(ref Loads));
        public ValueTask<int> GetDiscountAsync(int customerId) => ValueTask.FromResult(Interlocked.Increment(ref Loads));
        public ValueTask<int> GetStockAsync(int productId) => ValueTask.FromResult(Interlocked.Increment(ref Loads));
    }

    [Fact]
    public async Task UpsertGroupAsync_GivesEachMemberTheOverride_AndLeavesOtherMethodsAlone()
    {
        await using var provider = Build(out var shop);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        var service = provider.GetRequiredService<IShop>();

        await configurator.UpsertGroupAsync("pricing", o => o.WithEnabled(false), new RuntimeOverrideMetadata { Reason = "Incident 4211" });

        Assert.Equal([DiscountId, PriceId], configurator.GetOverrides().Select(static o => o.MethodId));
        Assert.All(configurator.GetOverrides(), o => Assert.Equal("Incident 4211", o.Metadata?.Reason));
        await service.GetPriceAsync(1);
        await service.GetPriceAsync(1);
        await service.GetStockAsync(1);
        await service.GetStockAsync(1);
        Assert.Equal(3, shop.Loads);

        await configurator.RemoveGroupAsync("pricing");

        Assert.Empty(configurator.GetOverrides());
        await service.GetPriceAsync(1);
        await service.GetPriceAsync(1);
        Assert.Equal(4, shop.Loads);
    }

    [Fact]
    public async Task UpsertAllAsync_GivesEveryCachedMethodTheOverride_AndClearRemovesThem()
    {
        await using var provider = Build(out _);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        await configurator.UpsertAllAsync(o => o.WithDuration(TimeSpan.FromMinutes(1)));

        Assert.Equal([DiscountId, PriceId, StockId], configurator.GetOverrides().Select(static o => o.MethodId));
        Assert.All(configurator.GetOverrides(), o => Assert.Equal(TimeSpan.FromMinutes(1), o.Options.Duration));

        await configurator.ClearAsync();
        Assert.Empty(configurator.GetOverrides());
    }

    [Fact]
    public async Task RemoveGroupAsync_RemovesOnlyTheGroupsOverrides()
    {
        await using var provider = Build(out _);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        await configurator.UpsertAllAsync(o => o.WithDuration(TimeSpan.FromMinutes(1)));

        await configurator.RemoveGroupAsync("pricing");

        Assert.Equal([StockId], configurator.GetOverrides().Select(static o => o.MethodId));
    }

    [Theory]
    [InlineData("Pricing", "did you mean 'pricing'")]
    [InlineData("prices", "did you mean 'pricing'")]
    public async Task UnknownGroup_Throws_NamingTheClosestGroup(string group, string hint)
    {
        await using var provider = Build(out _);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        var upsert = await Assert.ThrowsAsync<ArgumentException>(() => configurator.UpsertGroupAsync(group, o => o.WithEnabled(false)).AsTask());
        var remove = await Assert.ThrowsAsync<ArgumentException>(() => configurator.RemoveGroupAsync(group).AsTask());

        Assert.Contains(hint, upsert.Message, StringComparison.Ordinal);
        Assert.Contains("case-sensitive", upsert.Message, StringComparison.Ordinal);
        Assert.Contains(hint, remove.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverrideThatDoesNotFitOneMember_ChangesNoMethod()
    {
        await using var provider = Build(out _);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        // GetDiscountAsync has no productId parameter
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            configurator.UpsertGroupAsync("pricing", o => o.AddTags("product:{productId}")).AsTask());

        Assert.Contains(DiscountId, error.Message, StringComparison.Ordinal);
        Assert.Empty(configurator.GetOverrides());
    }

    [Fact]
    public async Task OverrideThatSetsNothing_Throws()
    {
        await using var provider = Build(out _);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => configurator.UpsertAsync(PriceId, _ => { }).AsTask());

        Assert.Contains("sets nothing", error.Message, StringComparison.Ordinal);
        Assert.Empty(configurator.GetOverrides());
    }

    [Fact]
    public async Task OverrideWithName_Throws()
    {
        await using var provider = Build(out _);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            configurator.UpsertAsync(PriceId, o => o.WithName("prices").WithDuration(TimeSpan.FromMinutes(1))).AsTask());

        Assert.Contains("WithName", error.Message, StringComparison.Ordinal);
        Assert.Empty(configurator.GetOverrides());
    }

    [Fact]
    public async Task OverrideThatHasAlreadyExpired_Throws()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
        await using var provider = Build(out _, time);
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => configurator.UpsertAsync(PriceId, o => o.WithEnabled(false),
            new RuntimeOverrideMetadata { ExpiresAt = time.GetUtcNow().AddMinutes(-1) }).AsTask());

        Assert.Contains("has already passed", error.Message, StringComparison.Ordinal);
        Assert.Empty(configurator.GetOverrides());
    }

    [Theory]
    [InlineData("20m", null, "the Duration comes from the method's other settings")]
    [InlineData(null, "5m", "the RefreshAhead comes from the method's other settings")]
    public async Task OverrideThatRefreshesAsSoonAsStored_Throws(string? refreshAhead, string? duration, string from)
    {
        await using var provider = Build(out _, configure: mc => mc.Method((IShop s) => s.GetStockAsync(0)).Configure(o => o.WithRefreshAhead(TimeSpan.FromMinutes(5))));
        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        var method = refreshAhead == null ? StockId : PriceId;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => configurator.UpsertAsync(method, o =>
        {
            if (refreshAhead != null) o.WithRefreshAhead(Parse(refreshAhead));
            if (duration != null) o.WithDuration(Parse(duration));
        }).AsTask());

        Assert.Contains(from, error.Message, StringComparison.Ordinal);
        Assert.Contains("RefreshAhead shorter than the Duration", error.Message, StringComparison.Ordinal);
    }

    private static TimeSpan Parse(string value) => TimeSpan.FromMinutes(int.Parse(value.TrimEnd('m'), System.Globalization.CultureInfo.InvariantCulture));

    private static ServiceProvider Build(out Shop shop, TimeProvider? time = null, Action<MethodCacheBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        shop = new Shop();
        services.AddSingleton<IShop>(shop);
        if (time != null) services.AddSingleton(time);
        services.AddMethodCache(mc => configure?.Invoke(mc));
        return services.BuildServiceProvider();
    }
}
