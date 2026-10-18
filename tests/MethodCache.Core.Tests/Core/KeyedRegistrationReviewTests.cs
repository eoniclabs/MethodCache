using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class KeyedRegistrationReviewTests
{
    [Fact]
    public void KeyedManager_DoesNotReplaceTheDefaultUnkeyedManager()
    {
        var services = new ServiceCollection();
        var custom = new NoOpCacheManager();
        services.AddKeyedSingleton<ICacheManager>("other", custom);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryCacheManager>(provider.GetRequiredService<ICacheManager>());
        Assert.Same(custom, provider.GetRequiredKeyedService<ICacheManager>("other"));
    }
}
