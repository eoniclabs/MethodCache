using System.Threading.Tasks;

namespace MethodCache.Core.Configuration
{
    public class CacheConfigurationService : ICacheConfigurationService
    {
        private readonly MethodCacheConfiguration _configuration;

        public CacheConfigurationService(MethodCacheConfiguration configuration)
        {
            _configuration = configuration;
        }

        public Task UpdateMethodConfigurationAsync(string methodId, CacheMethodSettings settings)
        {
            // This is a simplified implementation. In a real scenario, this would update
            // a persistent store and trigger a refresh of the in-memory configuration.
            // For now, we'll just update the in-memory settings directly.
            _configuration.SetMethodSettings(methodId, settings);
            return Task.CompletedTask;
        }

        public Task EnableCachingAsync(string methodId, bool enabled)
        {
            // This would typically involve updating a setting that controls whether caching is active for a method.
            // For now, we'll just log a message.
            System.Console.WriteLine($"Caching for method {methodId} {(enabled ? "enabled" : "disabled")}");
            return Task.CompletedTask;
        }

        public Task UpdateGlobalSettingsAsync(GlobalCacheSettings settings)
        {
            // This would typically involve updating global settings in a persistent store.
            // For now, we'll just update the in-memory settings directly.
            if (settings.DefaultDuration.HasValue)
            {
                _configuration.DefaultDuration(settings.DefaultDuration.Value);
            }
            if (settings.DefaultKeyGeneratorType != null)
            {
                // This requires a way to set the default key generator by type, not just by new()
                // _configuration.DefaultKeyGenerator(settings.DefaultKeyGeneratorType);
            }
            return Task.CompletedTask;
        }

        public Task InvalidateConfigurationCacheAsync()
        {
            // This would typically clear any cached configuration data and force a reload.
            // For now, we don't have a complex caching mechanism for configuration itself.
            return Task.CompletedTask;
        }
    }
}
