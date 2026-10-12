using System.Runtime.CompilerServices;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core.Extensions
{
    public class CacheManagerExtensionsTests
    {
        [Fact]
        public async Task VersionedManualEntry_FormattedKeyInvalidationReloads_AndContextsKeepLogicalKeys()
        {
            using var cache = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }));
            var contexts = new List<string>();
            var calls = 0;
            ValueTask<int> Load(CacheContext context, CancellationToken token) { contexts.Add(context.Key); return ValueTask.FromResult(++calls); }
            void Configure(CacheEntryOptions.Builder options) => options.WithVersion(3).OnHit(c => contexts.Add(c.Key));
            Assert.Equal(1, await cache.GetOrCreateAsync("logical", Load, Configure));
            Assert.Equal(1, await cache.GetOrCreateAsync("logical", Load, Configure));
            await cache.InvalidateByKeysAsync(CacheManagerExtensions.GetCacheKey("logical", 3));
            Assert.Equal(2, await cache.GetOrCreateAsync("logical", Load, Configure));
            Assert.All(contexts, key => Assert.Equal("logical", key));
            Assert.Equal("plain", CacheManagerExtensions.GetCacheKey("plain"));
            Assert.Equal("\0\0hostile", CacheManagerExtensions.GetCacheKey("\0hostile"));
            Assert.Throws<ArgumentOutOfRangeException>(() => CacheManagerExtensions.GetCacheKey("key", -1));
            Assert.Throws<ArgumentException>(() => CacheManagerExtensions.GetCacheKey(" "));
        }

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
        public async Task GetOrCreateAsync_WithTaskReturningCall_CachesUnderExplicitKey()
        {
            var cacheManager = new MockCacheManager();
            var calls = 0;

            Task<int> LoadAsync(int id)
            {
                calls++;
                return Task.FromResult(id * 2);
            }

            // The README pattern: an async lambda awaiting a Task-returning call
            var first = await cacheManager.GetOrCreateAsync(
                "order:21",
                async (ctx, ct) => await LoadAsync(21),
                o => o.WithDuration(TimeSpan.FromMinutes(5)).WithTags("orders"));
            var second = await cacheManager.GetOrCreateAsync(
                "order:21",
                async (ctx, ct) => await LoadAsync(21));
            var other = await cacheManager.GetOrCreateAsync(
                "order:22",
                async (ctx, ct) => await LoadAsync(22));

            Assert.Equal(42, first);
            Assert.Equal(42, second);
            Assert.Equal(44, other);
            Assert.Equal(2, calls);
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
            Assert.NotNull(cacheManager.LastPolicy);
            Assert.Equal(TimeSpan.FromMinutes(5), cacheManager.LastPolicy!.Duration);
            Assert.Contains("reports", cacheManager.LastPolicy.Tags);
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
        public async Task GetOrCreateAsync_MapsOptionsToRuntimePolicy()
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

            Assert.Equal("MethodCache.Fluent", cacheManager.LastMethodName);
            Assert.Equal("report:monthly", cacheManager.LastKey);

            Assert.NotNull(cacheManager.LastPolicy);
            Assert.Equal(duration, cacheManager.LastPolicy!.Duration);
            Assert.Contains("reports", cacheManager.LastPolicy.Tags);
            Assert.Contains("metrics", cacheManager.LastPolicy.Tags);
            Assert.Equal(TimeSpan.FromMinutes(2), cacheManager.LastPolicy.SlidingExpiration);
        }

        private static CacheRuntimePolicy CreateEmptyPolicy(string methodName) =>
            CacheRuntimePolicy.Empty(methodName);

        [Fact]
        public async Task InvalidateByKeysAsync_RemovesCachedEntry()
        {
            var cacheManager = new MockCacheManager();

            await cacheManager.GetOrCreateAsync(
                "user:invalidate",
                static (_, _) => new ValueTask<string>("cached"));

            await cacheManager.InvalidateByKeysAsync("user:invalidate");

            var lookup = await cacheManager.TryGetAsync<string>("user:invalidate");
            Assert.False(lookup.Found);
        }

        [Fact]
        public async Task GetOrCreateAsync_WhenPredicateBlocksCaching_DoesNotStoreValue()
        {
            var cacheManager = new MockCacheManager();
            var calls = 0;

            ValueTask<string> Factory(CacheContext context, CancellationToken token)
            {
                calls++;
                return new ValueTask<string>("value" + calls);
            }

            var first = await cacheManager.GetOrCreateAsync(
                "user:predicate",
                Factory,
                options => options.When(_ => false));

            var second = await cacheManager.GetOrCreateAsync(
                "user:predicate",
                Factory,
                options => options.When(_ => false));

            Assert.Equal("value1", first);
            Assert.Equal("value2", second);
        }

        [Fact]
        public async Task GetOrCreateAsync_WithVersion_UsesReservedVersionNamespace()
        {
            var cacheManager = new CapturingCacheManager();

            await cacheManager.GetOrCreateAsync(
                "user:version",
                static (_, _) => new ValueTask<int>(42),
                options => options.WithVersion(3));

            Assert.Equal("\0v3:user:version", cacheManager.LastKey);
        }

        // Enhanced Conditional Logic Tests
        [Fact]
        public async Task Cache_WithSimpleConditionalLogic_WorksCorrectly()
        {
            var cacheManager = new MockCacheManager();
            var userService = new MockUserService();
            var callCount = 0;

            // Use a simple approach with a fixed key to avoid key generation issues
            var cacheKey = "test-conditional-logic-key";

            // Test simple predicate that doesn't use arguments (for now)
            Func<CacheContext, bool> predicate = ctx => true;

            var result1 = await cacheManager.GetOrCreateAsync(
                cacheKey,
                (ctx, ct) => { callCount++; return userService.GetUserAsync(1); },
                configure: builder => builder.When(predicate)
            );

            var result2 = await cacheManager.GetOrCreateAsync(
                cacheKey,
                (ctx, ct) => { callCount++; return userService.GetUserAsync(1); },
                configure: builder => builder.When(predicate)
            );

            // Should cache when predicate returns true
            Assert.Equal(1, callCount); // Should be cached after first call
        }

        private sealed class CapturingCacheManager : ICacheManager
        {
            public string? LastMethodName { get; private set; }
            public string? LastKey { get; private set; }
            public CacheRuntimePolicy? LastPolicy { get; private set; }

            public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
                => GetOrCreateFastAsync(keyGenerator.GenerateKey(methodName, args, policy), methodName, factory, policy);

            /// <inheritdoc />
            public ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
                => new(CacheLookupResult<T>.Miss);

            public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
                    => new(default(T));

            public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
            {
                return new ValueTask<T?>(default(T));
            }

            public bool TryGetFast<T>(string cacheKey, out T? value)
            {
                value = default;
                return false;
            }

            public Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
            {
                LastKey = cacheKey;
                LastMethodName = methodName;
                LastPolicy = policy;
                return factory();
            }

            public Task InvalidateByTagsAsync(params string[] tags) => Task.CompletedTask;

            public Task InvalidateByKeysAsync(params string[] keys) => Task.CompletedTask;

            public Task InvalidateByTagPatternAsync(string pattern) => Task.CompletedTask;
        }

        // Mock service classes for testing
        public interface IUserService
        {
            ValueTask<User> GetUserAsync(int userId);
            ValueTask<UserProfile> GetUserProfileAsync(int userId);
        }

        public class MockUserService : IUserService
        {
            public ValueTask<User> GetUserAsync(int userId)
            {
                return new ValueTask<User>(new User { Id = userId, Name = $"User{userId}" });
            }

            public ValueTask<UserProfile> GetUserProfileAsync(int userId)
            {
                return new ValueTask<UserProfile>(new UserProfile { UserId = userId, Email = $"user{userId}@test.com" });
            }
        }

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        public class UserProfile
        {
            public int UserId { get; set; }
            public string Email { get; set; } = "";
        }
    }
}
