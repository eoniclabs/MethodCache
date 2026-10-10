using FluentAssertions;
using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure;
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

    [Fact]
    public async Task InvalidateByTagPatternAsync_IsNotSupported_AndDoesNotReportSuccess()
    {
        var manager = CreateManager(Substitute.For<IStorageProvider>());

        var act = () => manager.InvalidateByTagPatternAsync("user:*");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task GetOrCreateFastAsync_ReportsMissThenHit_ToMetricsProvider()
    {
        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetAsync<string>("key", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>((string?)null), new ValueTask<string?>("cached"));
        var metrics = Substitute.For<ICacheMetricsProvider>();
        var manager = CreateManager(storageProvider, metrics);
        var policy = CacheRuntimePolicy.Empty("HybridMethod") with { Duration = TimeSpan.FromMinutes(1) };

        await manager.GetOrCreateFastAsync("key", "HybridMethod", () => Task.FromResult("loaded"), policy);
        await manager.GetOrCreateFastAsync("key", "HybridMethod", () => Task.FromResult("loaded"), policy);

        metrics.Received(1).CacheMiss("HybridMethod");
        metrics.Received(1).CacheHit("HybridMethod");
        metrics.Received(1).CacheLatency("HybridMethod", Arg.Any<long>());
    }

    [Fact]
    public async Task GetOrCreateFastAsync_WhenFactoryThrows_ReportsError()
    {
        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetAsync<string>("key", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>((string?)null));
        var metrics = Substitute.For<ICacheMetricsProvider>();
        var manager = CreateManager(storageProvider, metrics);
        var policy = CacheRuntimePolicy.Empty("HybridMethod");

        var act = () => manager.GetOrCreateFastAsync<string>("key", "HybridMethod", () => throw new InvalidOperationException("boom"), policy);

        await act.Should().ThrowAsync<InvalidOperationException>();
        metrics.Received(1).CacheError("HybridMethod", "boom");
    }

    [Fact]
    public async Task GetOrCreateFastAsync_WhenStorageThrows_ReportsError()
    {
        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetAsync<string>("key", Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => throw new InvalidOperationException("storage down"));
        var metrics = Substitute.For<ICacheMetricsProvider>();
        var manager = CreateManager(storageProvider, metrics);
        var policy = CacheRuntimePolicy.Empty("HybridMethod");

        var act = () => manager.GetOrCreateFastAsync("key", "HybridMethod", () => Task.FromResult("value"), policy);

        await act.Should().ThrowAsync<InvalidOperationException>();
        metrics.Received(1).CacheError("HybridMethod", "storage down");
    }

    private static HybridCacheManager CreateManager(IStorageProvider storageProvider, ICacheMetricsProvider? metrics = null)
    {
        var l1Storage = Substitute.For<IMemoryStorage>();
        var options = Microsoft.Extensions.Options.Options.Create(new StorageOptions());
        return new HybridCacheManager(
            storageProvider,
            l1Storage,
            new DefaultCacheKeyGenerator(),
            NullLogger<HybridCacheManager>.Instance,
            options,
            metricsProvider: metrics);
    }
}
