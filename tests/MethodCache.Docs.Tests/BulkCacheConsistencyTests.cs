using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class BulkCacheConsistencyTests
{
    [Fact]
    public async Task BulkRead_WithDistributedHit_ReturnsTheSameValueAsAnIndividualRead()
    {
        var shared = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var time = new FakeTimeProvider();
        ServiceProvider Node()
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddMethodCache();
            services.AddMethodCacheDistributedCache(_ => shared);
            return services.BuildServiceProvider();
        }
        using var first = Node();
        using var second = Node();
        var writer = first.GetRequiredService<ICacheManager>();
        var reader = second.GetRequiredService<ICacheManager>();
        await writer.GetOrCreateAsync("key", (_, _) => ValueTask.FromResult("stored"));
        var bulk = await reader.GetOrCreateManyAsync<string>(["key"], (_, _, _) =>
            ValueTask.FromResult<IDictionary<string, string>>(new Dictionary<string, string> { ["key"] = "candidate" }));
        var individual = await reader.GetOrCreateAsync("key", (_, _) => ValueTask.FromResult("another"));
        Assert.Equal("stored", bulk["key"]);
        Assert.Equal(individual, bulk["key"]);
    }
}
