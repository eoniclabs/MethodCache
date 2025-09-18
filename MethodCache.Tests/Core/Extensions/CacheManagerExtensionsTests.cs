using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Extensions;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;
using Xunit;

namespace MethodCache.Tests.Core.Extensions
{
    public class CacheManagerExtensionsTests
    {
        [Fact]
        public async Task GetOrCreateAsync_UsesFactoryOnceAndCachesValue()
        {
            // Arrange
            var cacheManager = new MockCacheManager();
            var factoryCallCount = 0;

            ValueTask<string> Factory(CacheContext context, CancellationToken token)
            {
                factoryCallCount++;
                return new ValueTask<string>($"value:{context.Key}");
            }

            // Act
            var first = await cacheManager.GetOrCreateAsync("user:42", Factory);
            var second = await cacheManager.GetOrCreateAsync("user:42", Factory);

            // Assert
            Assert.Equal("value:user:42", first);
            Assert.Equal(first, second); // Same cached value
            Assert.Equal(1, factoryCallCount); // Factory invoked only once thanks to caching
        }

        [Fact]
        public async Task TryGetAsync_ReturnsMissThenHit()
        {
            // Arrange
            var cacheManager = new MockCacheManager();

            // Act - miss
            var miss = await cacheManager.TryGetAsync<string>("orders:active");

            // Populate cache via fluent helper
            await cacheManager.GetOrCreateAsync("orders:active", static (_, _) => new ValueTask<string>("cached"));

            // Act - hit
            var hit = await cacheManager.TryGetAsync<string>("orders:active");

            // Assert
            Assert.False(miss.Found);
            Assert.True(hit.Found);
            Assert.Equal("cached", hit.Value);
        }

        [Fact]
        public async Task GetOrCreateManyAsync_UsesBatchFactoryForMissingKeys()
        {
            // Arrange
            var cacheManager = new MockCacheManager();
            await cacheManager.GetOrCreateAsync("user:existing", static (_, _) => new ValueTask<string>("cached"));

            var factoryCalls = 0;
            ValueTask<IDictionary<string, string>> Factory(IReadOnlyList<string> missing, CacheContext context, CancellationToken token)
            {
                factoryCalls++;
                Assert.Equal(new[] { "user:missing", "user:another" }, missing);
                Assert.Equal("MethodCache.Fluent.Bulk", context.Key);

                IDictionary<string, string> results = new Dictionary<string, string>
                {
                    ["user:missing"] = "value-missing",
                    ["user:another"] = "value-another"
                };

                return new ValueTask<IDictionary<string, string>>(results);
            }

            // Act
            var values = await cacheManager.GetOrCreateManyAsync(
                new[] { "user:existing", "user:missing", "user:another" },
                Factory);

            // Assert
            Assert.Equal(3, values.Count);
            Assert.Equal("cached", values["user:existing"]);
            Assert.Equal("value-missing", values["user:missing"]);
            Assert.Equal("value-another", values["user:another"]);
            Assert.Equal(1, factoryCalls);

            var lookup = await cacheManager.TryGetAsync<string>("user:missing");
            Assert.True(lookup.Found);
            Assert.Equal("value-missing", lookup.Value);
        }

        [Fact]
        public async Task GetOrCreateManyAsync_AppliesConfigureToNewEntries()
        {
            var cacheManager = new CapturingCacheManager();

            ValueTask<IDictionary<string, string>> Factory(IReadOnlyList<string> missing, CacheContext context, CancellationToken token)
            {
                IDictionary<string, string> results = new Dictionary<string, string>
                {
                    ["report:1"] = "cached-report"
                };

                return new ValueTask<IDictionary<string, string>>(results);
            }

            await cacheManager.GetOrCreateManyAsync(
                new[] { "report:1" },
                Factory,
                options => options.WithDuration(TimeSpan.FromMinutes(5)).WithTags("reports"));

            Assert.Equal("MethodCache.Fluent", cacheManager.LastMethodName);
            Assert.NotNull(cacheManager.LastSettings);
            Assert.Equal(TimeSpan.FromMinutes(5), cacheManager.LastSettings!.Duration);
            Assert.Contains("reports", cacheManager.LastSettings.Tags);
        }

        [Fact]
        public async Task GetOrCreateManyAsync_ThrowsWhenFactoryOmitsKey()
        {
            var cacheManager = new MockCacheManager();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await cacheManager.GetOrCreateManyAsync(
                    new[] { "missing" },
                    (missing, _, _) => new ValueTask<IDictionary<string, string>>(new Dictionary<string, string>())));
        }

        [Fact]
        public async Task GetOrCreateAsync_InvokesOnHitAndOnMissCallbacks()
        {
            // Arrange
            var cacheManager = new MockCacheManager();
            var hitCount = 0;
            var missCount = 0;

            ValueTask<string> Factory(CacheContext context, CancellationToken token)
                => new("value");

            // Act - initial miss populates cache
            await cacheManager.GetOrCreateAsync(
                "users:1",
                Factory,
                options => options.OnHit(_ => hitCount++).OnMiss(_ => missCount++));

            // Second call should use cache
            await cacheManager.GetOrCreateAsync(
                "users:1",
                Factory,
                options => options.OnHit(_ => hitCount++).OnMiss(_ => missCount++));

            // Assert
            Assert.Equal(1, missCount);
            Assert.Equal(1, hitCount);
        }

        [Fact]
        public async Task GetOrCreateAsync_MapsOptionsToLegacySettings()
        {
            // Arrange
            var cacheManager = new CapturingCacheManager();
            var duration = TimeSpan.FromMinutes(5);
            var builtOptions = new CacheEntryOptions.Builder()
                .WithDuration(duration)
                .WithSlidingExpiration(TimeSpan.FromMinutes(2))
                .Build();
            Assert.Equal(TimeSpan.FromMinutes(2), builtOptions.SlidingExpiration);

            // Act
            var result = await cacheManager.GetOrCreateAsync(
                "report:monthly",
                static (_, _) => new ValueTask<int>(99),
                options => options
                    .WithDuration(duration)
                    .WithSlidingExpiration(TimeSpan.FromMinutes(2))
                    .WithTags("reports", "metrics"));

            // Assert runtime result
            Assert.Equal(99, result);

            // Assert legacy pipeline mapping
            Assert.Equal("MethodCache.Fluent", cacheManager.LastMethodName);
            Assert.NotNull(cacheManager.LastArgs);
            Assert.Empty(cacheManager.LastArgs!);

            Assert.NotNull(cacheManager.LastSettings);
            Assert.Equal(duration, cacheManager.LastSettings!.Duration);
            Assert.Contains("reports", cacheManager.LastSettings.Tags);
            Assert.Contains("metrics", cacheManager.LastSettings.Tags);
            Assert.True(cacheManager.LastSettings.IsIdempotent);
            Assert.Equal(TimeSpan.FromMinutes(2), cacheManager.LastSettings.SlidingExpiration);

            // Ensure the fixed key generator preserves the fluent key
            Assert.NotNull(cacheManager.LastKeyGenerator);
            var generatedKey = cacheManager.LastKeyGenerator!.GenerateKey("ignored", Array.Empty<object>(), new CacheMethodSettings());
            Assert.Equal("report:monthly", generatedKey);
        }

        private sealed class CapturingCacheManager : ICacheManager
        {
            public string? LastMethodName { get; private set; }
            public object[]? LastArgs { get; private set; }
            public CacheMethodSettings? LastSettings { get; private set; }
            public ICacheKeyGenerator? LastKeyGenerator { get; private set; }

            public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
            {
                LastMethodName = methodName;
                LastArgs = args;
                LastSettings = settings;
                LastKeyGenerator = keyGenerator;
                return factory();
            }

            public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
            {
                LastMethodName = methodName;
                LastArgs = args;
                LastSettings = settings;
                LastKeyGenerator = keyGenerator;
                return new ValueTask<T?>(default(T));
            }

            public Task InvalidateByTagsAsync(params string[] tags) => Task.CompletedTask;
        }
    }
}
