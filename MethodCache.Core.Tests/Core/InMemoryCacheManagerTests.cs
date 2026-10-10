using FluentAssertions;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Runtime.KeyGeneration;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class InMemoryCacheManagerTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithDefaultValueTypeResult_ExecutesFactoryOnMiss()
    {
        var manager = new InMemoryCacheManager(new NullCacheMetricsProvider());
        var policy = CacheRuntimePolicy.Empty("MethodA") with { Duration = TimeSpan.FromMinutes(1) };
        var keyGenerator = new DefaultCacheKeyGenerator();
        var calls = 0;

        Task<int> Factory()
        {
            calls++;
            return Task.FromResult(0);
        }

        var first = await manager.GetOrCreateAsync("MethodA", Array.Empty<object>(), Factory, policy, keyGenerator);
        var second = await manager.GetOrCreateAsync("MethodA", Array.Empty<object>(), Factory, policy, keyGenerator);

        first.Should().Be(0);
        second.Should().Be(0);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ClearAsync_RemovesEntriesAndAllowsReload()
    {
        var manager = new InMemoryCacheManager(new NullCacheMetricsProvider());
        var policy = CacheRuntimePolicy.Empty("MethodB") with
        {
            Duration = TimeSpan.FromMinutes(1)
        };
        var keyGenerator = new DefaultCacheKeyGenerator();
        var calls = 0;

        Task<string> Factory() => Task.FromResult($"value{++calls}");

        (await manager.GetOrCreateAsync("MethodB", Array.Empty<object>(), Factory, policy, keyGenerator)).Should().Be("value1");

        await manager.ClearAsync();

        (await manager.GetStatsAsync()).Entries.Should().Be(0);
        (await manager.GetOrCreateAsync("MethodB", Array.Empty<object>(), Factory, policy, keyGenerator)).Should().Be("value2");
    }
}
