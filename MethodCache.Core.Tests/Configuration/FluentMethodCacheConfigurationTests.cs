using System;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.Infrastructure.Metrics;
using MethodCache.Core.Options;
using MethodCache.Core.PolicyPipeline.Model;
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
        configuration.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(1)).RefreshAhead(TimeSpan.FromSeconds(10)));
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

    public static TheoryData<string, Action<CacheEntryOptions.Builder>> GetOrCreateOnlySettings => new()
    {
        { "OnHit", o => o.OnHit(_ => { }) },
        { "OnMiss", o => o.OnMiss(_ => { }) },
        { "WithMetrics", o => o.WithMetrics(new NoopMetrics()) },
        { "When", o => o.When(_ => true) },
    };

    [Theory]
    [MemberData(nameof(GetOrCreateOnlySettings))]
    public void BuildMethodPolicies_Throws_ForSettingsThatOnlyApplyToGetOrCreate(string setting, Action<CacheEntryOptions.Builder> configure)
    {
        var configuration = new FluentMethodCacheConfiguration();
        configuration.ForService<IOrderService>()
            .Method(s => s.GetOrderAsync(default))
            .Configure(configure);

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.BuildMethodPolicies());

        Assert.Contains($"{setting}()", exception.Message);
        Assert.Contains(nameof(IOrderService.GetOrderAsync), exception.Message);
    }

    [Fact]
    public void BuildMethodPolicies_Throws_WhenDefaultPolicyUsesGetOrCreateOnlySetting()
    {
        var configuration = new FluentMethodCacheConfiguration();
        configuration.DefaultPolicy(o => o.OnHit(_ => { }));

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.BuildMethodPolicies());

        Assert.Contains("DefaultPolicy()", exception.Message);
    }

    private sealed class NoopMetrics : ICacheMetrics
    {
        public void RecordHit(string key, TimeSpan? duration, object? value) { }
        public void RecordMiss(string key) { }
        public void RecordError(string key, Exception exception) { }
    }
}
