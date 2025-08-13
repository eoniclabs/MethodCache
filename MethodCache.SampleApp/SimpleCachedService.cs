using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp
{
    // Simple demonstration service showing how to use MethodCache APIs
    public interface ISimpleCachedService
    {
        Task<string> GetExpensiveDataAsync(string key);
        Task<User?> GetUserAsync(int userId);
        Task InvalidateUserCacheAsync();
        Task InvalidateAllCacheAsync();
    }

    public class SimpleCachedService : ISimpleCachedService
    {
        private readonly ICacheManager _cacheManager;
        private readonly ICacheKeyGenerator _keyGenerator;
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly CacheMethodSettings _defaultSettings;
        private readonly Random _random = new();

        public SimpleCachedService(ICacheManager cacheManager, ICacheKeyGenerator keyGenerator, ICacheMetricsProvider metricsProvider)
        {
            _cacheManager = cacheManager;
            _keyGenerator = keyGenerator;
            _metricsProvider = metricsProvider;
            _defaultSettings = new CacheMethodSettings
            {
                Duration = TimeSpan.FromMinutes(5)
            };
        }

        public async Task<string> GetExpensiveDataAsync(string key)
        {
            return await _cacheManager.GetOrCreateAsync(
                methodName: "GetExpensiveDataAsync",
                args: new object[] { key },
                factory: async () =>
                {
                    // Simulate expensive operation
                    Console.WriteLine($"[CACHE MISS] Generating expensive data for key '{key}'...");
                    await Task.Delay(_random.Next(500, 1500));
                    var expensiveData = $"Expensive data for '{key}' generated at {DateTime.Now:HH:mm:ss.fff}";
                    Console.WriteLine($"[FACTORY] Generated data for key '{key}'");
                    return expensiveData;
                },
                settings: _defaultSettings,
                keyGenerator: _keyGenerator,
                requireIdempotent: false
            );
        }

        public async Task<User?> GetUserAsync(int userId)
        {
            var userSettings = new CacheMethodSettings
            {
                Duration = TimeSpan.FromMinutes(10),
                Tags = new List<string> { "users", $"user-{userId}" }
            };

            return await _cacheManager.GetOrCreateAsync(
                methodName: "GetUserAsync",
                args: new object[] { userId },
                factory: async () =>
                {
                    // Simulate database lookup
                    Console.WriteLine($"[CACHE MISS] Looking up user {userId} in database...");
                    await Task.Delay(_random.Next(100, 300));
                    
                    var user = new User
                    {
                        Id = userId,
                        Name = $"User {userId}",
                        Email = $"user{userId}@example.com",
                        CreatedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 365)),
                        IsActive = true
                    };
                    
                    Console.WriteLine($"[FACTORY] Created user {userId}");
                    return user;
                },
                settings: userSettings,
                keyGenerator: _keyGenerator,
                requireIdempotent: false
            );
        }

        public async Task InvalidateUserCacheAsync()
        {
            await _cacheManager.InvalidateByTagsAsync("users");
            Console.WriteLine("[CACHE INVALIDATE] Invalidated all user cache entries by tag");
        }

        public async Task InvalidateAllCacheAsync()
        {
            // Note: The base ICacheManager doesn't have a clear all method
            // This would require a custom implementation or invalidating by tags
            await _cacheManager.InvalidateByTagsAsync("users", "data");
            Console.WriteLine("[CACHE INVALIDATE] Invalidated cache entries by tags");
        }
    }
}