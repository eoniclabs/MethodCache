
using System;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;

namespace MethodCache.SampleApp
{
    public class SampleService : ISampleService
    {
        private readonly ICacheManager _cacheManager;
        private readonly ICacheMetricsProvider _metricsProvider;

        public SampleService(ICacheManager cacheManager, ICacheMetricsProvider metricsProvider)
        {
            _cacheManager = cacheManager;
            _metricsProvider = metricsProvider;
        }

        public virtual async Task<string> GetDataAsync(string key)
        {
            Console.WriteLine($"Executing GetDataAsync for key: {key}");
            await Task.Delay(100);
            return $"Some data for {key}";
        }

        public virtual Task InvalidateDataAsync(string key)
        {
            return Task.CompletedTask;
        }

        public virtual async Task<string> GetUserDataAsync(int userId)
        {
            Console.WriteLine($"Executing GetUserDataAsync for user: {userId}");
            await Task.Delay(100);
            return $"User data for {userId}";
        }

        public virtual Task InvalidateUserCacheAsync(int userId)
        {
            return Task.CompletedTask;
        }
    }
}
