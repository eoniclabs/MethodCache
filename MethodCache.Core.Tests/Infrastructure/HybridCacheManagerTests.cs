using FluentAssertions;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Core.Storage.Coordination;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MethodCache.Core.Tests.Infrastructure;

public class HybridCacheManagerTests
{
    [Fact]
    public void TryGetFast_WithDefaultValueTypeHit_ReturnsTrue()
    {
        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetAsync<int>("key", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<int>(0));
        storageProvider.ExistsAsync("key", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var manager = CreateManager(storageProvider);

        var hit = manager.TryGetFast<int>("key", out var value);

        hit.Should().BeTrue();
        value.Should().Be(0);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithDefaultValueTypeMiss_InvokesFactory()
    {
        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetAsync<int>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<int>(0));
        storageProvider.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(false));

        var manager = CreateManager(storageProvider);
        var keyGenerator = new DefaultCacheKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("HybridMethod") with { Duration = TimeSpan.FromMinutes(1) };
        var calls = 0;

        Task<int> Factory()
        {
            calls++;
            return Task.FromResult(42);
        }

        var value = await manager.GetOrCreateAsync("HybridMethod", Array.Empty<object>(), Factory, policy, keyGenerator);

        value.Should().Be(42);
        calls.Should().Be(1);
        await storageProvider.Received(1).SetAsync(Arg.Any<string>(), 42, Arg.Any<TimeSpan>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    private static HybridCacheManager CreateManager(IStorageProvider storageProvider)
    {
        var l1Storage = Substitute.For<IMemoryStorage>();
        var options = Microsoft.Extensions.Options.Options.Create(new StorageOptions());
        return new HybridCacheManager(
            storageProvider,
            l1Storage,
            new DefaultCacheKeyGenerator(),
            NullLogger<HybridCacheManager>.Instance,
            options);
    }
}
