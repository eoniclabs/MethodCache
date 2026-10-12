using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class AtomicCustomCacheTests
{
    [Fact]
    public void CustomResultCacheWithoutAtomicStampContract_IsRejectedWhenManagerIsResolved()
    {
        using var provider = new ServiceCollection().AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddMethodCache().AddMethodCacheDistributedCache(_ => new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())))
            .BuildServiceProvider();

        var error = Assert.Throws<ArgumentException>(() => provider.GetRequiredService<ICacheManager>());

        Assert.Contains("IAtomicInvalidationStore", error.Message, StringComparison.Ordinal);
    }
}
