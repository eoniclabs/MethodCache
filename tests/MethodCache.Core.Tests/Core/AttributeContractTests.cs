using MethodCache.Generated;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class AttributeContractTests
{
    public interface ISettings
    {
        [Cache] int Inherited();
        [Cache(Enabled = false, Distributed = false, Version = 0, FailSafe = " off ", RetryAfter = "2s")]
        int Explicit();
        [Cache(Enabled = true, Distributed = true)] int Enabled();
    }
    public sealed class Settings : ISettings
    {
        public int Inherited() => 1;
        public int Explicit() => 2;
        public int Enabled() => 3;
    }

    [Fact]
    public void NamedArgumentPresence_PreservesUnsetAndExplicitFalse()
    {
        var descriptors = GeneratedCode.Describe([typeof(ISettings).Assembly], [typeof(ISettings)])
            .SelectMany(module => module.Policies).Where(policy => policy.MethodId.Contains("AttributeContractTests.ISettings.", StringComparison.Ordinal)).ToDictionary(policy => policy.MethodId.Split('.').Last());
        Assert.Null(descriptors["Inherited"].Policy.Enabled);
        Assert.Null(descriptors["Inherited"].Policy.Distributed);
        Assert.Null(descriptors["Inherited"].Policy.Version);
        Assert.False(descriptors["Explicit"].Policy.Enabled);
        Assert.False(descriptors["Explicit"].Policy.Distributed);
        Assert.Equal(0, descriptors["Explicit"].Policy.Version);
        Assert.Equal(TimeSpan.Zero, descriptors["Explicit"].Policy.FailSafe);
        Assert.Equal(TimeSpan.FromSeconds(2), descriptors["Explicit"].Policy.FailSafeRetryAfter);
        Assert.True(descriptors["Enabled"].Policy.Enabled);
        Assert.True(descriptors["Enabled"].Policy.Distributed);
    }

    public interface ITagged
    {
        [Cache(Tags = ["literal:unchanged\\text", "value:{value}"])] Task<int> Read(object? value);
        [CacheInvalidate(Tags = ["value:{value}"])] Task Reset(object? value);
    }
    public sealed class Tagged : ITagged
    {
        public int Loads;
        public Task<int> Read(object? value) => Task.FromResult(++Loads);
        public Task Reset(object? value) => Task.CompletedTask;
    }

    [Fact]
    public async Task GeneratedTagInvalidation_MatchesCanonicalScalarValues()
    {
        var inner = new Tagged();
        var services = new ServiceCollection();
        services.AddSingleton<ITagged>(inner);
        services.AddMethodCache(builder => builder.FromAssemblyOf<AttributeContractTests>());
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ITagged>();
        object?[] values = [null, "null", "\\0", "a:b\\c\0\ud800", Guid.NewGuid(),
            new DateTimeOffset(2026, 10, 15, 12, 13, 14, TimeSpan.FromHours(2)).AddTicks(1234),
            new DateTime(2026, 10, 15, 12, 13, 14, DateTimeKind.Utc).AddTicks(1234), DayOfWeek.Friday, false];
        foreach (var value in values)
        {
            var first = await service.Read(value);
            Assert.Equal(first, await service.Read(value));
            await service.Reset(value);
            Assert.Equal(first + 1, await service.Read(value));
        }
        Assert.Equal(values.Length * 2, inner.Loads);
        Assert.All(provider.GetRequiredService<ICacheAdministrator>().ListEntries(),
            entry => Assert.Contains("literal:unchanged\\text", entry.Tags));
    }

    [Fact]
    public void ContractOne_OmittedTagCapability_RetainsLegacyFormattingAndSeparateIdentity()
    {
        var legacy = new GeneratedModuleBuilder(new EmptyModule());
        legacy.Method("Read", ["value"], null, new GeneratedMethodOptions { Tags = ["tag:{value}"] });
        var canonical = new GeneratedModuleBuilder(new EmptyModule());
        canonical.Method("Read", ["value"], null, new GeneratedMethodOptions { Tags = ["tag:{value}"], CanonicalTagParts = true });
        var oldPolicy = CacheRuntimePolicy.FromPolicy("Read", legacy.Policies.Single().Policy, MethodCache.Policies.CachePolicyFields.None);
        var newPolicy = CacheRuntimePolicy.FromPolicy("Read", canonical.Policies.Single().Policy, MethodCache.Policies.CachePolicyFields.None);
        var id = Guid.Parse("e4cf16cb-b221-4c3d-8231-d78a2e126978");
        Assert.Equal("tag:" + id.ToString(), CacheTagTemplates.Expand(oldPolicy, ["value"], [(object)id]).Tags.Single());
        Assert.Equal("tag:" + MethodCacheKeys.Part(id), CacheTagTemplates.Expand(newPolicy, ["value"], [(object)id]).Tags.Single());
        Assert.NotEqual(PolicyIdentity.Shared(oldPolicy), PolicyIdentity.Shared(newPolicy));
    }

    private sealed class EmptyModule : GeneratedModule
    {
        public override void Describe(GeneratedModuleBuilder builder) { }
    }
}
