using System;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.Infrastructure.Metrics;
using MethodCache.Core.Options;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class FluentMethodCacheConfigurationTests
{
    public interface IOrderService
    {
        Task<string> GetOrderAsync(int id);
    }

    [Fact]
    public void BuildMethodPolicies_MapsExpirationSettings_InDefaultGroupMethodOrder()
    {
        var configuration = new FluentMethodCacheConfiguration();
        configuration.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(1)).RefreshAhead(TimeSpan.FromSeconds(10)));
        configuration.ForGroup("orders").Configure(o => o.WithSlidingExpiration(TimeSpan.FromMinutes(2)));
        configuration.ForService<IOrderService>()
            .Method(s => s.GetOrderAsync(default))
            .WithGroup("orders")
            .Configure(o => o.WithDuration(TimeSpan.FromMinutes(5)));

        var draft = Assert.Single(configuration.BuildMethodPolicies());

        Assert.Equal(TimeSpan.FromMinutes(5), draft.Policy.Duration);
        Assert.Equal(TimeSpan.FromMinutes(2), draft.Policy.SlidingExpiration);
        Assert.Equal(TimeSpan.FromSeconds(10), draft.Policy.RefreshAhead);
        Assert.True(draft.Fields.HasFlag(CachePolicyFields.SlidingExpiration));
        Assert.True(draft.Fields.HasFlag(CachePolicyFields.RefreshAhead));
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
        configuration.ForService<IOrderService>().Method(s => s.GetOrderAsync(default));

        Assert.Throws<InvalidOperationException>(() => configuration.BuildMethodPolicies());
    }

    private sealed class NoopMetrics : ICacheMetrics
    {
        public void RecordHit(string key, TimeSpan? duration, object? value) { }
        public void RecordMiss(string key) { }
        public void RecordError(string key, Exception exception) { }
    }
}
