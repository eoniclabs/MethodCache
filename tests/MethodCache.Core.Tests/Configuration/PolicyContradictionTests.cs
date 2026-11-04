using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// Settings that can't be honored fail when they are set, never on a later call: a <c>Duration</c> longer than
/// <see cref="MethodCacheMemoryOptions.MaxExpiration"/>, and a <c>CacheWhen&lt;T&gt;</c> for a type the method's result
/// can never be.
/// </summary>
public class PolicyContradictionTests
{
    private static readonly string LongId = MethodIds.For(typeof(IContradictions), nameof(IContradictions.GetLong));
    private static readonly string CountId = MethodIds.For(typeof(IContradictions), nameof(IContradictions.CountAsync));
    private static readonly TimeSpan TooLong = TimeSpan.FromDays(8);

    public interface IContradictions
    {
        [Cache(Duration = "30d")] int GetLong(int id);
        [Cache] Task<int> CountAsync(int id);
        [Cache] object Describe(int id);
    }

    public interface IShort
    {
        [Cache] int Get(int id);
    }

    public sealed class Contradictions : IContradictions
    {
        public int Calls { get; private set; }
        public int GetLong(int id) => ++Calls;
        public Task<int> CountAsync(int id) => Task.FromResult(++Calls);
        public object Describe(int id) => "item" + id;
    }

    public sealed class Short : IShort
    {
        public int Get(int id) => id;
    }

    [Fact]
    public void AttributeDuration_LongerThanMaxExpiration_FailsStartup()
    {
        using var provider = Build(services => services.AddSingleton<IContradictions, Contradictions>());

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains($"The Duration 30.00:00:00 set in [Cache] on '{LongId}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("MethodCacheMemoryOptions.MaxExpiration (7.00:00:00)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeDuration_WithinARaisedMaxExpiration_IsKeptForItsWholeDuration()
    {
        var time = new FakeTimeProvider();
        var service = new Contradictions();
        using var provider = Build(services => services.AddSingleton<TimeProvider>(time).AddSingleton<IContradictions>(service),
            mc => mc.Memory(o => o.MaxExpiration = TimeSpan.FromDays(60)));
        var cached = provider.GetRequiredService<IContradictions>();

        cached.GetLong(1);
        time.Advance(TimeSpan.FromDays(29));
        cached.GetLong(1);

        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public void AttributeDuration_LongerThanMaxExpiration_IsAllowedWithTheApplicationsOwnManager()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICacheManager, NoOpCacheManager>();
        services.AddSingleton<IContradictions, Contradictions>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();

        Assert.NotEmpty(provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());
    }

    [Theory]
    [InlineData("defaults")]
    [InlineData("group")]
    [InlineData("method")]
    public void CodeDuration_LongerThanMaxExpiration_FailsStartup(string layer)
    {
        using var provider = Build(services => services.AddSingleton<IShort, Short>(), mc =>
        {
            switch (layer)
            {
                case "defaults": mc.Defaults(o => o.WithDuration(TooLong)); break;
                case "group": mc.Group("slow", o => o.WithDuration(TooLong)); break;
                default: mc.Method((IShort s) => s.Get(0)).Configure(o => o.WithDuration(TooLong)); break;
            }
        });

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains("The Duration 8.00:00:00 set in mc.", error.Message, StringComparison.Ordinal);
        Assert.Contains("MethodCacheMemoryOptions.MaxExpiration (7.00:00:00)", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MethodCache:Defaults:Duration")]
    [InlineData("MethodCache:Groups:slow:Duration")]
    [InlineData("MethodCache:Methods:{0}:Duration")]
    public void ConfigurationDuration_LongerThanMaxExpiration_FailsStartup_NamingThePath(string path)
    {
        var key = string.Format(System.Globalization.CultureInfo.InvariantCulture, path, MethodIds.For(typeof(IShort), nameof(IShort.Get)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "8d" }).Build();
        using var provider = Build(services => services.AddSingleton<IShort, Short>(), mc => mc.FromConfiguration(configuration));

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains($"'{key}' has value '8d', which is not at most MethodCacheMemoryOptions.MaxExpiration (7.00:00:00)",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationReload_DurationLongerThanMaxExpiration_IsRejectedAndThePreviousPolicyStays()
    {
        var key = $"MethodCache:Methods:{MethodIds.For(typeof(IShort), nameof(IShort.Get))}:Duration";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "1h" }).Build();
        using var provider = Build(services => services.AddSingleton<IShort, Short>(), mc => mc.FromConfiguration(configuration));
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        var id = MethodIds.For(typeof(IShort), nameof(IShort.Get));
        Assert.Equal(TimeSpan.FromHours(1), registry.GetPolicy(id).Policy.Duration);

        configuration[key] = "8d";
        configuration.Reload();

        Assert.Equal(TimeSpan.FromHours(1), registry.GetPolicy(id).Policy.Duration);
    }

    [Fact]
    public async Task RuntimeOverrideDuration_LongerThanMaxExpiration_Throws_AndChangesNothing()
    {
        await using var provider = Build(services => services.AddSingleton<IShort, Short>());
        var runtime = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await runtime.UpsertAsync<IShort>(s => s.Get(0), o => o.WithDuration(TooLong)));

        Assert.Contains("MethodCacheMemoryOptions.MaxExpiration (7.00:00:00)", error.Message, StringComparison.Ordinal);
        Assert.Empty(runtime.GetOverrides());
    }

    [Fact]
    public async Task ManualDuration_LongerThanMaxExpiration_ThrowsBeforeTheFactoryRuns()
    {
        await using var provider = Build(_ => { });
        var cache = provider.GetRequiredService<ICacheManager>();
        var calls = 0;

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await cache.GetOrCreateAsync("report", _ => ValueTask.FromResult(++calls), o => o.WithDuration(TooLong)));

        Assert.Contains("MethodCacheMemoryOptions.MaxExpiration (7.00:00:00)", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ManualBulkDuration_LongerThanMaxExpiration_ThrowsBeforeTheFactoryRuns()
    {
        await using var provider = Build(_ => { });
        var cache = provider.GetRequiredService<ICacheManager>();
        var calls = 0;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await cache.GetOrCreateManyAsync<int>(["a"], (keys, _) =>
        {
            calls++;
            return ValueTask.FromResult<IReadOnlyDictionary<string, int>>(keys.ToDictionary(key => key, _ => 1));
        }, o => o.WithDuration(TooLong)));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RuntimeOverrideCacheWhen_ForAnotherResultType_Throws_AndTheMethodKeepsCaching()
    {
        var service = new Contradictions();
        await using var provider = Build(services => services.AddSingleton<IContradictions>(service), mc => mc.Memory(o => o.MaxExpiration = TimeSpan.FromDays(60)));
        var runtime = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        var typed = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await runtime.UpsertAsync<IContradictions>(s => s.CountAsync(0), o => o.CacheWhen<string>(text => text.Length > 0)));
        var byId = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await runtime.UpsertAsync(CountId, o => o.CacheWhen<string>(text => text.Length > 0)));
        var cached = provider.GetRequiredService<IContradictions>();
        await cached.CountAsync(1);
        await cached.CountAsync(1);

        Assert.Contains("CacheWhen<String> can't check the Int32 result", typed.Message, StringComparison.Ordinal);
        Assert.Contains(CountId, byId.Message, StringComparison.Ordinal);
        Assert.Empty(runtime.GetOverrides());
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task RuntimeOverrideCacheWhen_ForTheResultTypeOrABaseType_IsAccepted()
    {
        await using var provider = Build(services => services.AddSingleton<IContradictions, Contradictions>(), mc => mc.Memory(o => o.MaxExpiration = TimeSpan.FromDays(60)));
        var runtime = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        await runtime.UpsertAsync<IContradictions>(s => s.CountAsync(0), o => o.CacheWhen<int>(count => count > 0));
        await runtime.UpsertAsync<IContradictions>(s => s.Describe(0), o => o.CacheWhen<string>(text => text.Length > 0));

        Assert.Equal(2, runtime.GetOverrides().Count);
    }

    [Fact]
    public void CodeCacheWhen_ForAnotherResultType_FailsStartup()
    {
        using var provider = Build(services => services.AddSingleton<IContradictions, Contradictions>(), mc =>
        {
            mc.Memory(o => o.MaxExpiration = TimeSpan.FromDays(60));
            mc.Method((IContradictions s) => s.CountAsync(0)).Configure(o => o.CacheWhen<string>(text => text.Length > 0));
        });

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains("CacheWhen<String> can't check the Int32 result", error.Message, StringComparison.Ordinal);
        Assert.Contains(CountId, error.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(Action<IServiceCollection> register, Action<MethodCacheBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        register(services);
        services.AddMethodCache(mc => configure?.Invoke(mc));
        return services.BuildServiceProvider();
    }
}
