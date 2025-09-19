using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.Runtime
{
    /// <summary>
    /// Configuration source that monitors IOptions changes for runtime reconfiguration
    /// </summary>
    public class OptionsMonitorConfigurationSource : IConfigurationSource, IDisposable
    {
        private readonly IOptionsMonitor<MethodCacheOptions> _optionsMonitor;
        private readonly Action<MethodCacheOptions>? _onChangeCallback;
        private IDisposable? _changeListener;
        
        public OptionsMonitorConfigurationSource(
            IOptionsMonitor<MethodCacheOptions> optionsMonitor,
            Action<MethodCacheOptions>? onChangeCallback = null)
        {
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _onChangeCallback = onChangeCallback;
            
            // Subscribe to changes
            _changeListener = _optionsMonitor.OnChange((options, name) =>
            {
                _onChangeCallback?.Invoke(options);
            });
        }
        
        public int Priority => 40; // Highest priority - runtime management should override everything
        
        public bool SupportsRuntimeUpdates => true;
        
        public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync()
        {
            var entries = new List<MethodCacheConfigEntry>();
            var options = _optionsMonitor.CurrentValue;
            
            // Process service-specific configurations
            foreach (var serviceKvp in options.Services)
            {
                var serviceName = serviceKvp.Key;
                var serviceOptions = serviceKvp.Value;
                
                foreach (var methodKvp in serviceOptions.Methods)
                {
                    var methodName = methodKvp.Key;
                    var methodOptions = methodKvp.Value;
                    
                    // Skip if explicitly disabled
                    if (methodOptions.Enabled == false)
                        continue;
                    
                    var settings = new CacheMethodSettings
                    {
                        Duration = methodOptions.Duration 
                            ?? serviceOptions.DefaultDuration 
                            ?? options.DefaultDuration,
                        Tags = CombineTags(
                            options.GlobalTags,
                            serviceOptions.DefaultTags,
                            methodOptions.Tags),
                        Version = methodOptions.Version
                    };
                    
                    // Apply ETag settings
                    var etagOptions = methodOptions.ETag ?? options.ETag;
                    if (etagOptions != null)
                    {
                        settings.ETag = new ETagSettings
                        {
                            Strategy = Enum.TryParse<ETagGenerationStrategy>(etagOptions.Strategy, out var strategy)
                                ? strategy
                                : ETagGenerationStrategy.ContentHash,
                            IncludeParametersInETag = etagOptions.IncludeParametersInETag,
                            UseWeakETag = etagOptions.UseWeakETag,
                            Metadata = etagOptions.Metadata?.ToArray(),
                            CacheDuration = etagOptions.CacheDuration
                        };
                    }
                    
                    entries.Add(new MethodCacheConfigEntry
                    {
                        ServiceType = serviceName,
                        MethodName = methodName,
                        Settings = settings
                    });
                }
            }
            
            return Task.FromResult(entries.AsEnumerable());
        }
        
        private List<string> CombineTags(params List<string>[] tagLists)
        {
            var combined = new HashSet<string>();
            
            foreach (var list in tagLists.Where(l => l != null))
            {
                foreach (var tag in list)
                {
                    combined.Add(tag);
                }
            }
            
            return combined.ToList();
        }
        
        public void Dispose()
        {
            _changeListener?.Dispose();
        }
    }
}
