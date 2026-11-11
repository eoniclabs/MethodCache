using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// The rules for policies set in appsettings and code: every id names a cached method, each method is configured once,
/// settings that only describe a method stay on its entry, and tag templates fit the methods they reach.
/// </summary>
public class ConfigFileRulesTests
{
    private static readonly string GetId = MethodIds.For(typeof(IRules), nameof(IRules.GetAsync));
    private static readonly string OrderId = MethodIds.For(typeof(IRules), nameof(IRules.GetOrderAsync));
    private static readonly string LinesId = MethodIds.For(typeof(IRules), nameof(IRules.GetOrderLinesAsync));

    public interface IRules
    {
        [Cache] ValueTask<int> GetAsync(int id);
        [Cache(Group = "orders")] ValueTask<int> GetOrderAsync(int orderId);
        [Cache(Group = "orders")] ValueTask<int> GetOrderLinesAsync(int orderId);
        ValueTask<int> NotCachedAsync(int id);
        Task SaveAsync(int id);
    }

    // A cached service another host registers: startup code and configuration shared by several hosts name it
    public interface IOtherHostRules
    {
        [Cache] ValueTask<int> FindAsync(int id);
    }

    public sealed class Rules : IRules
    {
        public ValueTask<int> GetAsync(int id) => ValueTask.FromResult(id);
        public ValueTask<int> GetOrderAsync(int orderId) => ValueTask.FromResult(orderId);
        public ValueTask<int> GetOrderLinesAsync(int orderId) => ValueTask.FromResult(orderId);
        public ValueTask<int> NotCachedAsync(int id) => ValueTask.FromResult(id);
        public Task SaveAsync(int id) => Task.CompletedTask;
    }

    [Fact]
    public void SameMethodIdTwice_FailsStartup_NamingBothPaths()
    {
        var error = LoadFails(new()
        {
            [$"MethodCache:Methods:{GetId}:Duration"] = "1m",
            ["MethodCache:Methods:second:MethodId"] = GetId,
            ["MethodCache:Methods:second:Duration"] = "2m"
        });

        Assert.Contains($"'MethodCache:Methods:{GetId}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'MethodCache:Methods:second'", error.Message, StringComparison.Ordinal);
        Assert.Contains("configure the same method", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("group:orders")]
    public void DefaultsIdUsedAsAMethodId_FailsStartup(string id)
    {
        var error = LoadFails(new()
        {
            ["MethodCache:Methods:0:MethodId"] = id,
            ["MethodCache:Methods:0:Duration"] = "1m"
        });

        Assert.Contains($"'MethodCache:Methods:0:MethodId' has value '{id}', which is not a method id", error.Message, StringComparison.Ordinal);
        Assert.Contains("Groups:<name>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsIdUsedAsAMethodKey_FailsStartup()
    {
        var error = LoadFails(new() { ["MethodCache:Methods:*:Duration"] = "1m" });

        Assert.StartsWith("MethodCache configuration 'MethodCache:Methods:*' is not a method id", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Defaults", "Group", "orders")]
    [InlineData("Groups:orders", "Group", "other")]
    public void MethodOnlySettingOutsideAMethod_FailsStartup(string section, string setting, string value)
    {
        var error = LoadFails(new() { [$"MethodCache:{section}:{setting}"] = value });

        Assert.Contains($"'MethodCache:{section}:{setting.Split(':')[0]}' can't be set here", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FailSafe", "Duration")]
    [InlineData("FailSafeTimeout", "Timeout")]
    [InlineData("FailSafeRetryAfter", "RetryAfter")]
    public void FailSafeValueInDefaultsAndTheFailSafeBlock_FailsStartup(string defaultsSetting, string blockSetting)
    {
        var error = LoadFails(new()
        {
            [$"MethodCache:Defaults:{defaultsSetting}"] = "1m",
            ["MethodCache:FailSafe:Duration"] = "10m",
            [$"MethodCache:FailSafe:{blockSetting}"] = "5s"
        });

        Assert.Contains($"'MethodCache:Defaults:{defaultsSetting}' and 'MethodCache:FailSafe:{blockSetting}'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Methods:{0}", "Notes", "why")]
    [InlineData("Methods:{0}", "Metadata:owner", "team-a")]
    [InlineData("Defaults", "Notes", "why")]
    [InlineData("Groups:orders", "Metadata:owner", "team-a")]
    public void NotesAndMetadata_FailStartup_PointingToAComment(string section, string setting, string value)
    {
        section = string.Format(System.Globalization.CultureInfo.InvariantCulture, section, GetId);
        var error = LoadFails(new() { [$"MethodCache:{section}:{setting}"] = value });

        Assert.Contains($"'MethodCache:{section}:{setting.Split(':')[0]}' is not a setting MethodCache reads", error.Message, StringComparison.Ordinal);
        Assert.Contains("// comment", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Distributed")]
    [InlineData("Duration")]
    [InlineData("Version")]
    [InlineData("FailSafe")]
    [InlineData("ClearTags")]
    [InlineData("Group")]
    [InlineData("Tags:0")]
    public void EmptyValue_FailsStartup_NamingTheSetting(string setting)
    {
        var error = LoadFails(new() { [$"MethodCache:Methods:{GetId}:{setting}"] = " " });

        Assert.Contains($"'MethodCache:Methods:{GetId}:{setting}' is empty", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FailSafe:Timeout")]
    [InlineData("Defaults:FailSafe:Timeout")]
    public void FailSafeErrors_SuggestTheUnitFormat(string setting)
    {
        var error = LoadFails(new() { [$"MethodCache:{setting}"] = "1m" });

        Assert.DoesNotContain("01:00:00", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"1h\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicesSection_IsNotRead_AsMethodsWas_Renamed()
    {
        var error = LoadFails(new() { [$"MethodCache:Services:{GetId}:Duration"] = "1m" });

        Assert.Contains("'MethodCache:Services' is not a setting MethodCache knows", error.Message, StringComparison.Ordinal);
        Assert.Contains("Methods", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMethodKey_FailsStartup_WithTheClosestCachedMethod()
    {
        var typo = GetId.Replace("GetAsync", "GetAsnc", StringComparison.Ordinal);

        var error = LoadFails(new() { [$"MethodCache:Methods:{typo}:Duration"] = "1m" });

        Assert.Contains($"'MethodCache:Methods:{typo}' configures a method that isn't cached", error.Message, StringComparison.Ordinal);
        Assert.Contains($"did you mean '{GetId}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMethodIdValue_FailsStartup_WithTheClosestCachedMethod()
    {
        var typo = OrderId.Replace("GetOrderAsync", "GetOrdersAsync", StringComparison.Ordinal);

        var error = LoadFails(new()
        {
            ["MethodCache:Methods:0:MethodId"] = typo,
            ["MethodCache:Methods:0:Duration"] = "1m"
        });

        Assert.Contains("'MethodCache:Methods:0:MethodId'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"did you mean '{OrderId}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadWithAnUnknownMethod_IsRejected_AndThePreviousPoliciesStay()
    {
        var configuration = Configuration(new() { [$"MethodCache:Methods:{GetId}:Duration"] = "1m" });
        using var provider = Build(configuration);
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy(GetId).Policy.Duration);

        configuration[$"MethodCache:Methods:{GetId}:Duration"] = "5m";
        configuration["MethodCache:Methods:typo:Duration"] = "2m";
        configuration.Reload();

        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy(GetId).Policy.Duration);
        Assert.DoesNotContain(registry.GetAllPolicies(), policy => policy.MethodId == "typo");
    }

    [Fact]
    public void GroupsSection_SetsTheGroupsDefaults_AndTheGroupKeyJoinsAMethodToIt()
    {
        var configuration = Configuration(new()
        {
            ["MethodCache:Groups:orders:Duration"] = "2m",
            ["MethodCache:Groups:orders:Tags:0"] = "orders",
            [$"MethodCache:Methods:{GetId}:Group"] = "orders"
        });
        using var provider = Build(configuration);
        var registry = provider.GetRequiredService<IPolicyRegistry>();

        foreach (var id in new[] { OrderId, LinesId, GetId })
        {
            var policy = registry.GetPolicy(id).Policy;
            Assert.Equal(TimeSpan.FromMinutes(2), policy.Duration);
            Assert.Contains("orders", policy.Tags);
        }
    }

    [Fact]
    public void GroupsSection_WinsOverTheSameGroupInCode_ButNotOverAMethodsOwnSettings()
    {
        var configuration = Configuration(new() { ["MethodCache:Groups:orders:Duration"] = "2m" });
        using var provider = Build(configuration, mc =>
        {
            mc.Group("orders", o => o.WithDuration(TimeSpan.FromMinutes(1)).WithVersion(3));
            mc.Method((IRules r) => r.GetOrderLinesAsync(0)).Configure(o => o.WithDuration(TimeSpan.FromSeconds(30)));
        });
        var registry = provider.GetRequiredService<IPolicyRegistry>();

        var order = registry.GetPolicy(OrderId);
        Assert.Equal(TimeSpan.FromMinutes(2), order.Policy.Duration);
        Assert.Equal(3, order.Policy.Version);
        Assert.Equal(TimeSpan.FromSeconds(30), registry.GetPolicy(LinesId).Policy.Duration);
    }

    [Fact]
    public void ConfiguredTagNamingNoParameter_FailsStartup_NamingTheTag()
    {
        var error = LoadFails(new()
        {
            [$"MethodCache:Methods:{GetId}:Tags:0"] = "items",
            [$"MethodCache:Methods:{GetId}:Tags:1"] = "item:{itemId}"
        });

        Assert.Contains($"'MethodCache:Methods:{GetId}:Tags:1' has value 'item:{{itemId}}'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"fits '{GetId}': a placeholder names no parameter of the method, whose parameters are id", error.Message, StringComparison.Ordinal);
        Assert.Contains("MC0010", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredDefaultTag_ThatSomeMethodLacks_FailsStartup()
    {
        var error = LoadFails(new() { ["MethodCache:Defaults:Tags:0"] = "order:{orderId}" });

        Assert.Contains("'MethodCache:Defaults:Tags:0'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"fits '{GetId}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredGroupTag_FitsEveryMethodInTheGroup_AndAMethodCanClearIt()
    {
        var configuration = Configuration(new()
        {
            ["MethodCache:Groups:orders:Tags:0"] = "order:{OrderId}",
            ["MethodCache:Defaults:Tags:0"] = "all:{orderId}",
            [$"MethodCache:Methods:{GetId}:ClearTags"] = "true"
        });
        using var provider = Build(configuration);

        Assert.Contains("order:{OrderId}", provider.GetRequiredService<IPolicyRegistry>().GetPolicy(OrderId).Policy.Tags);
        Assert.Empty(provider.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId).Policy.Tags);
    }

    [Fact]
    public void ReloadThatPutsAMethodInAGroupWhoseCodeTagDoesNotFit_IsRejected()
    {
        var configuration = Configuration(new() { [$"MethodCache:Methods:{GetId}:Duration"] = "1m" });
        using var provider = Build(configuration, mc => mc.Group("orders", o => o.AddTags("order:{orderId}")));
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy(GetId).Policy.Duration);

        configuration[$"MethodCache:Methods:{GetId}:Group"] = "orders";
        configuration[$"MethodCache:Methods:{GetId}:Duration"] = "5m";
        configuration.Reload();

        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy(GetId).Policy.Duration);
        Assert.False(registry.GetPolicy(GetId).Policy.Metadata.ContainsKey("group"));
    }

    [Fact]
    public void GroupKeyPuttingAMethodWhereACodeTagDoesNotFit_FailsStartup()
    {
        var configuration = Configuration(new() { [$"MethodCache:Methods:{GetId}:Group"] = "orders" });
        using var provider = Build(configuration, mc => mc.Group("orders", o => o.AddTags("order:{orderId}")));

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains($"'MethodCache:Methods:{GetId}' lets a tag set in mc.Group(\"orders\", ...) reach its method", error.Message, StringComparison.Ordinal);
        Assert.Contains("but a placeholder names no parameter of the method, whose parameters are id", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("order:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingSources_CreditEachSourceOnlyForTheSettingsItSets_ByPropertyName()
    {
        var configuration = Configuration(new()
        {
            [$"MethodCache:Methods:{OrderId}:SlidingExpiration"] = "1m",
            [$"MethodCache:Methods:{OrderId}:ClearTags"] = "true"
        });
        await using var provider = Build(configuration, mc => mc.Group("orders", o => o.WithVersion(2).WithDuration(TimeSpan.FromMinutes(3))));
        await provider.GetRequiredService<IRuntimeCacheConfigurator>()
            .UpsertAsync<IRules>(r => r.GetOrderAsync(Any<int>.Value), o => o.WithFailSafeRetryAfter(TimeSpan.FromSeconds(5)));

        var method = provider.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == OrderId);

        Assert.Equal(new Dictionary<string, CacheSettingSource>
        {
            ["Duration"] = CacheSettingSource.Code,
            ["Version"] = CacheSettingSource.Code,
            ["SlidingExpiration"] = CacheSettingSource.Configuration,
            ["Tags"] = CacheSettingSource.Configuration,
            ["FailSafeRetryAfter"] = CacheSettingSource.RuntimeOverride
        }, method.SettingSources);
        Assert.Equal("{\"Duration\":\"Code\"}", System.Text.Json.JsonSerializer.Serialize(
            method.SettingSources.Where(pair => pair.Key == "Duration").ToDictionary()));
    }

    [Fact]
    public void ReloadWithABadTag_IsRejected_AndThePreviousPoliciesStay()
    {
        var configuration = Configuration(new() { [$"MethodCache:Methods:{GetId}:Tags:0"] = "item:{id}" });
        using var provider = Build(configuration);
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        Assert.Contains("item:{id}", registry.GetPolicy(GetId).Policy.Tags);

        configuration[$"MethodCache:Methods:{GetId}:Tags:0"] = "item:{item.Id}";
        configuration.Reload();

        Assert.Contains("item:{id}", registry.GetPolicy(GetId).Policy.Tags);
    }

    [Theory]
    [InlineData("defaults", "mc.Defaults(...)")]
    [InlineData("group", "mc.Group(\"orders\", ...)")]
    [InlineData("method", "mc.Method(...)")]
    public void CodeTagNamingNoParameter_FailsStartup(string where, string described)
    {
        var services = Services(mc =>
        {
            switch (where)
            {
                case "defaults": mc.Defaults(o => o.AddTags("tenant:{tenantId}")); break;
                case "group": mc.Group("orders", o => o.AddTags("tenant:{tenantId}")); break;
                default: mc.Method((IRules r) => r.GetAsync(0)).Configure(o => o.AddTags("tenant:{tenantId}")); break;
            }
        });
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains($"The tag 'tenant:{{tenantId}}' set in {described} reaches the cached method", error.Message, StringComparison.Ordinal);
        Assert.Contains("MC0010", error.Message, StringComparison.Ordinal);
    }

    public interface ICaseRules
    {
        [Cache] ValueTask<int> FindAsync(int name, int Name);
    }

    public sealed class CaseRules : ICaseRules
    {
        public ValueTask<int> FindAsync(int name, int Name) => ValueTask.FromResult(name + Name);
    }

    [Theory]
    [InlineData("found:{NAME}", false)]
    [InlineData("found:{Name}", true)]
    public void CodeTagMatchingSeveralParametersOnlyIgnoringCase_FailsStartup(string tag, bool accepted)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICaseRules, CaseRules>();
        services.AddMethodCache(mc => mc.Method((ICaseRules r) => r.FindAsync(0, 0)).Configure(o => o.AddTags(tag)));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPolicyRegistry>();

        if (accepted)
        {
            Assert.Contains(tag, registry.GetPolicy(MethodIds.For(typeof(ICaseRules), nameof(ICaseRules.FindAsync))).Policy.Tags);
            return;
        }

        var error = Assert.Throws<InvalidOperationException>(() => registry.GetAllPolicies());
        Assert.Contains("{NAME} matches the parameters name, Name when case is ignored", error.Message, StringComparison.Ordinal);
        Assert.Contains("MC0017", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeTagWithAParameterInAnotherCase_IsAccepted()
    {
        using var provider = Services(mc => mc.Method((IRules r) => r.GetAsync(0)).Configure(o => o.AddTags("item:{ID}"))).BuildServiceProvider();

        Assert.Contains("item:{ID}", provider.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId).Policy.Tags);
    }

    [Fact]
    public void MethodWithoutCacheInCode_FailsStartup_WithTheClosestCachedMethod()
    {
        using var provider = Services(mc => mc.Method((IRules r) => r.NotCachedAsync(0)).Configure(o => o.WithDuration(TimeSpan.FromMinutes(1))))
            .BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.StartsWith("mc.Method(...) configures a method that isn't cached.", error.Message, StringComparison.Ordinal);
        Assert.Contains($"'{MethodIds.For(typeof(IRules), nameof(IRules.NotCachedAsync))}' is not a cached method; did you mean", error.Message, StringComparison.Ordinal);
        Assert.Contains(MethodIds.Format, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CodePolicyForACachedServiceThisHostDoesNotRegister_IsSkipped_AndLoggedAtInformation()
    {
        var logs = new InformationLog();
        var services = Services(mc =>
        {
            mc.Method((IOtherHostRules r) => r.FindAsync(0)).CacheWhen(value => value > 0);
            mc.Method((IRules r) => r.GetAsync(0)).Configure(o => o.WithDuration(TimeSpan.FromMinutes(3)));
        });
        services.AddLogging(b => b.AddProvider(logs));
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IPolicyRegistry>();

        Assert.Equal(TimeSpan.FromMinutes(3), registry.GetPolicy(GetId).Policy.Duration);
        var otherId = MethodIds.For(typeof(IOtherHostRules), nameof(IOtherHostRules.FindAsync));
        Assert.Contains(logs.Lines, line => line.Contains($"'{otherId}', whose service isn't registered in this application", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredPolicyForACachedServiceThisHostDoesNotRegister_IsSkipped()
    {
        var otherId = MethodIds.For(typeof(IOtherHostRules), nameof(IOtherHostRules.FindAsync));
        using var provider = Build(Configuration(new()
        {
            [$"MethodCache:Methods:{otherId}:Duration"] = "1m",
            [$"MethodCache:Methods:{GetId}:Duration"] = "2m"
        }));

        var registry = provider.GetRequiredService<IPolicyRegistry>();

        Assert.Equal(TimeSpan.FromMinutes(2), registry.GetPolicy(GetId).Policy.Duration);
    }

    [Fact]
    public void MethodWithoutAResultInCode_ThrowsWhenConfigured()
    {
        var error = Assert.Throws<ArgumentException>(() => Services(mc => mc.Method((IRules r) => r.SaveAsync(0))));

        Assert.Contains("returns Task without a result, so it has nothing to cache", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeOverrideTagNamingNoParameter_Throws_AndChangesNothing()
    {
        await using var provider = Services().BuildServiceProvider();
        var runtime = provider.GetRequiredService<IRuntimeCacheConfigurator>();

        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await runtime.UpsertAsync<IRules>(r => r.GetOrderAsync(0), o => o.AddTags("order:{id}")));
        await runtime.UpsertAsync<IRules>(r => r.GetOrderAsync(0), o => o.AddTags("order:{orderId}"));

        Assert.Contains("The tag 'order:{id}' in the override reaches the cached method", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "order:{orderId}" }, Assert.Single(runtime.GetOverrides()).Options.Tags);
    }

    [Fact]
    public void ClearTagsInCodeDefaults_FailsStartup_AsInConfiguration()
    {
        using var provider = Services(mc => mc.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(1)).ClearTags())).BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains("ClearTags is set in mc.Defaults(...)", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("defaults")]
    [InlineData("group")]
    public void WithNameInCode_FailsStartup(string where)
    {
        using var provider = Services(mc =>
        {
            Action<CacheEntryOptions.Builder> named = o => o.WithName("orders").WithDuration(TimeSpan.FromMinutes(1));
            if (where == "method") mc.Method((IRules r) => r.GetAsync(0)).Configure(named);
            else if (where == "defaults") mc.Defaults(named);
            else mc.Group("orders", named);
        }).BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains("WithName is set in mc.", error.Message, StringComparison.Ordinal);
    }

    private static InvalidOperationException LoadFails(Dictionary<string, string?> settings)
    {
        using var provider = Build(Configuration(settings));
        return Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());
    }

    private static IConfigurationRoot Configuration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static ServiceProvider Build(IConfiguration configuration, Action<MethodCacheBuilder>? configure = null)
        => Services(mc => { configure?.Invoke(mc); mc.FromConfiguration(configuration); }).BuildServiceProvider();

    private static ServiceCollection Services(Action<MethodCacheBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRules, Rules>();
        services.AddMethodCache(mc => configure?.Invoke(mc));
        return services;
    }

    private sealed class InformationLog : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) lock (_lines) _lines.Add(formatter(state, exception));
        }

        public void Dispose() { }
    }
}
