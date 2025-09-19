using System.Threading.Tasks;

namespace MethodCache.Core.Configuration
{
    public interface ICacheConfigurationService
    {
        Task UpdateMethodConfigurationAsync(string methodId, CacheMethodSettings settings);
        Task EnableCachingAsync(string methodId, bool enabled);
        Task UpdateGlobalSettingsAsync(GlobalCacheSettings settings);
        Task InvalidateConfigurationCacheAsync();
    }
}
