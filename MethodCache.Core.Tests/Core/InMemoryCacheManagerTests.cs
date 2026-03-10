using System.Collections.Concurrent;
using System.Reflection;
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
    public async Task ClearAsync_RemovesDistributedLockState()
    {
        var manager = new InMemoryCacheManager(new NullCacheMetricsProvider());
        var policy = CacheRuntimePolicy.Empty("MethodB") with
        {
            Duration = TimeSpan.FromMinutes(1),
            DistributedLock = new DistributedLockOptions(TimeSpan.FromSeconds(5), 2)
        };
        var keyGenerator = new DefaultCacheKeyGenerator();

        await manager.GetOrCreateAsync("MethodB", Array.Empty<object>(), () => Task.FromResult("value"), policy, keyGenerator);

        GetDistributedLockCount(manager).Should().Be(1);

        await manager.ClearAsync();

        GetDistributedLockCount(manager).Should().Be(0);
    }

    private static int GetDistributedLockCount(InMemoryCacheManager manager)
    {
        var field = typeof(InMemoryCacheManager).GetField("_distributedLocks", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        var value = field!.GetValue(manager);
        value.Should().NotBeNull();
        var dictionary = value as ConcurrentDictionary<string, object>;
        if (dictionary != null)
        {
            return dictionary.Count;
        }

        // The value type is private; use reflection to read Count.
        var countProperty = value!.GetType().GetProperty("Count");
        countProperty.Should().NotBeNull();
        return (int)(countProperty!.GetValue(value) ?? 0);
    }
}
