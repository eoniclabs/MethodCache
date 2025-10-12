using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;

namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Service for managing cache configuration at runtime.
    /// Note: This interface is currently not implemented and serves as a placeholder for future dynamic configuration features.
    /// </summary>
    public interface ICacheConfigurationService
    {
        Task UpdateMethodConfigurationAsync(string methodId, CachePolicy policy);
        Task EnableCachingAsync(string methodId, bool enabled);
        Task UpdateGlobalSettingsAsync(GlobalCacheSettings settings);
        Task InvalidateConfigurationCacheAsync();
    }
}
