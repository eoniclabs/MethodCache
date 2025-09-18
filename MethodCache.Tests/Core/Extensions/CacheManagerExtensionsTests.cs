using System;
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
        public async Task GetOrCreateAsync_MapsOptionsToLegacySettings()
        {
            // Arrange
            var cacheManager = new CapturingCacheManager();
            var duration = TimeSpan.FromMinutes(5);

            // Act
            var result = await cacheManager.GetOrCreateAsync(
                "report:monthly",
                static (_, _) => new ValueTask<int>(99),
                options => options.WithDuration(duration).WithTags("reports", "metrics"));

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
