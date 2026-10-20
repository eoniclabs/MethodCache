using System;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Extensibility;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class FluentMethodCacheConfigurationTests
{
    public interface IOrderService
    {
        Task<string> GetOrderAsync(int id);
    }

    [Fact]
    public void BuildMethodPolicies_KeepsDefaultsAndGroupsApartFromMethods()
    {
        var configuration = new FluentMethodCacheConfiguration();
        configuration.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(1)).WithRefreshAhead(TimeSpan.FromSeconds(10)));
        configuration.ForGroup("orders").Configure(o => o.WithSlidingExpiration(TimeSpan.FromMinutes(2)));
        configuration.ForService<IOrderService>()
            .Method(s => s.GetOrderAsync(default))
            .WithGroup("orders")
            .Configure(o => o.WithDuration(TimeSpan.FromMinutes(5)));

        var drafts = configuration.BuildMethodPolicies().ToDictionary(d => d.MethodId);

        Assert.Equal(TimeSpan.FromMinutes(1), drafts[MethodIds.AllMethods].Policy.Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), drafts[MethodIds.AllMethods].Policy.RefreshAhead);
        Assert.Equal(TimeSpan.FromMinutes(2), drafts[MethodIds.Group("orders")].Policy.SlidingExpiration);

        var method = drafts[MethodIds.For(typeof(IOrderService), nameof(IOrderService.GetOrderAsync))];
        Assert.Equal(TimeSpan.FromMinutes(5), method.Policy.Duration);
        Assert.False(method.Fields.HasFlag(CachePolicyFields.RefreshAhead));
        Assert.Equal("orders", method.Policy.Metadata["group"]);
    }

    [Fact]
    public void BuildMethodPolicies_UnifiedOptions_KeepExplicitFlagsAndTypedPredicate()
    {
        var configuration = new FluentMethodCacheConfiguration();
        configuration.DefaultPolicy(o => o.WithEnabled(false).WithDistributed(false));
        configuration.ForService<IOrderService>().Method(s => s.GetOrderAsync(default))
            .Configure(o => o.WithEnabled(true).CacheWhen<string>(value => value.Length > 0));
        var drafts = configuration.BuildMethodPolicies().ToDictionary(d => d.MethodId);
        Assert.False(drafts[MethodIds.AllMethods].Policy.Enabled);
        Assert.False(drafts[MethodIds.AllMethods].Policy.Distributed);
        var method = drafts[MethodIds.For(typeof(IOrderService), nameof(IOrderService.GetOrderAsync))];
        Assert.True(method.Policy.Enabled);
        Assert.True(method.Policy.CacheWhen!("value"));
        Assert.False(method.Policy.CacheWhen!(""));
    }

    [Fact]
    public void BuildMethodPolicies_TypedCacheWhenThenConfigure_TheLaterConfigureWins()
    {
        var configuration = new FluentMethodCacheConfiguration();
        configuration.ForService<IOrderService>().Method(s => s.GetOrderAsync(default))
            .CacheWhen(value => value.Length > 0)
            .Configure(o => o.CacheWhen<string>(value => value.Length > 5));

        var method = configuration.BuildMethodPolicies().Single(d => d.MethodId == MethodIds.For(typeof(IOrderService), nameof(IOrderService.GetOrderAsync)));

        Assert.False(method.Policy.CacheWhen!("abc"));
        Assert.True(method.Policy.CacheWhen!("abcdef"));
    }

    [Fact]
    public void BuildMethodPolicies_ConfigureThenTypedCacheWhen_TheLaterTypedCallWins()
    {
        var configuration = new FluentMethodCacheConfiguration();
        configuration.ForService<IOrderService>().Method(s => s.GetOrderAsync(default))
            .Configure(o => o.CacheWhen<string>(value => value.Length > 5))
            .CacheWhen(value => value.Length > 0);

        var method = configuration.BuildMethodPolicies().Single(d => d.MethodId == MethodIds.For(typeof(IOrderService), nameof(IOrderService.GetOrderAsync)));

        Assert.True(method.Policy.CacheWhen!("abc"));
        Assert.False(method.Policy.CacheWhen!(""));
    }

    [Theory]
    [InlineData(false, "mc.Defaults(...)")]
    [InlineData(true, "mc.Group(\"orders\", ...)")]
    public void CacheWhenInDefaultsOrAGroup_FailsWhenThePoliciesLoad(bool inGroup, string where)
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc =>
        {
            if (inGroup) mc.Group("orders", o => o.CacheWhen<string>(value => value.Length > 0));
            else mc.Defaults(o => o.CacheWhen<string>(value => value.Length > 0));
        });
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains($"CacheWhen is set in {where}", error.Message, StringComparison.Ordinal);
    }
}
