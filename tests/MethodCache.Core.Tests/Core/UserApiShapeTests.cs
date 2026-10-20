using MethodCache.Extensibility;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class UserApiShapeTests
{
    [Theory]
    [InlineData("IFluentMethodCacheConfiguration")]
    [InlineData("IFluentServiceConfiguration`1")]
    [InlineData("IFluentGroupConfiguration")]
    public void ReplacedStartupInterfaces_AreNotExported(string typeName)
    {
        var exported = typeof(MethodCacheBuilder).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, type => type.Name == typeName);
        Assert.Contains(typeof(MethodCacheBuilder), exported);
        Assert.Contains(typeof(IFluentMethodConfiguration<>), exported);
    }

    [Fact]
    public void UserInterfaces_ExposeOnlyFourMethods_WithCallerTokenLast()
    {
        Assert.Equal(new[] { "GetOrCreateAsync", "InvalidateKeysAsync", "InvalidateTagsAsync", "TryGetAsync" },
            typeof(ICacheManager).GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.Equal(new[] { "ClearAsync", "GetOverrides", "RemoveAsync", "UpsertAsync" },
            typeof(IRuntimeCacheConfigurator).GetMethods().Select(m => m.Name).Order().ToArray());
        foreach (var type in new[] { typeof(ICacheManager), typeof(IRuntimeCacheConfigurator), typeof(CacheManagerExtensions), typeof(RuntimeCacheConfiguratorExtensions) })
            foreach (var method in type.GetMethods().Where(m => m.ReturnType == typeof(Task) || m.Name.EndsWith("Async", StringComparison.Ordinal)))
                Assert.Equal(typeof(CancellationToken), method.GetParameters().Last().ParameterType);
        foreach (var name in new[] { "OnHit", "OnMiss", "When", "WithMetrics" })
            Assert.DoesNotContain(typeof(CacheEntryOptions.Builder).GetMethods(), m => m.Name == name);
        Assert.DoesNotContain(typeof(ICacheManager).Assembly.GetExportedTypes(), t => t.Name == "CacheContext");
        Assert.True(typeof(NoOpCacheManager).IsSealed);
        Assert.Equal(new[] { "CacheNullValues", "JitterMaxDuration", "MaxExpiration", "MaxHttpBytes", "MaxItems", "Weigher", "WeightLimit" },
            typeof(MethodCacheMemoryOptions).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void BuiltOptions_AreImmutable_AndCustomManagersCanCheckTypedPredicates()
    {
        var input = new[] { "first" };
        var builder = new CacheEntryOptions.Builder().AddTags(input).WithEnabled(true).CacheWhen<int>(value => value > 0);
        var options = builder.Build();
        input[0] = "changed";
        builder.AddTags("later").WithEnabled(false);
        Assert.Equal(new[] { "first" }, options.Tags);
        Assert.True(options.CanCache(1));
        Assert.False(options.CanCache(0));
        Assert.False(builder.Build().CanCache(1));
        Assert.False(new CacheEntryOptions.Builder().CacheWhen<int>(_ => throw new InvalidOperationException()).Build().CanCache(1));
        Assert.True(new CacheEntryOptions.Builder().Build().CanCache<string?>(null));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)options.Tags)[0] = "changed");
    }
}
