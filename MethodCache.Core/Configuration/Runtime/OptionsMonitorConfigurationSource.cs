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

                    var metadata = MergeMetadata(
                        options.ETag,
                        serviceOptions.ETag,
                        methodOptions.ETag);

                    if (metadata != null)
                    {
                        settings.SetETagMetadata(metadata);
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

        private static ETagMetadata ConvertToMetadata(ETagOptions options)
        {
            var metadata = new ETagMetadata
            {
                Strategy = options.Strategy,
                IncludeParametersInETag = options.IncludeParametersInETag,
                UseWeakETag = options.UseWeakETag,
                Metadata = options.Metadata?.Count > 0 ? options.Metadata.ToArray() : null,
                CacheDuration = options.CacheDuration
            };

            if (!string.IsNullOrWhiteSpace(options.ETagGeneratorType))
            {
                metadata.ETagGeneratorType = Type.GetType(options.ETagGeneratorType!);
            }

            return metadata;
        }

        private static ETagMetadata? MergeMetadata(ETagOptions? global, ETagOptions? service, ETagOptions? method)
        {
            ETagMetadata? current = null;

            if (global != null)
            {
                current = ConvertToMetadata(global);
            }

            if (service != null)
            {
                current = Merge(current, ConvertToMetadata(service));
            }

            if (method != null)
            {
                current = Merge(current, ConvertToMetadata(method));
            }

            return current;
        }

        private static ETagMetadata Merge(ETagMetadata? baseline, ETagMetadata overlay)
        {
            var result = baseline != null ? (ETagMetadata)baseline.Clone() : new ETagMetadata();

            if (!string.IsNullOrWhiteSpace(overlay.Strategy))
            {
                result.Strategy = overlay.Strategy;
            }

            if (overlay.IncludeParametersInETag.HasValue)
            {
                result.IncludeParametersInETag = overlay.IncludeParametersInETag;
            }

            if (overlay.ETagGeneratorType != null)
            {
                result.ETagGeneratorType = overlay.ETagGeneratorType;
            }

            if (overlay.Metadata != null && overlay.Metadata.Length > 0)
            {
                result.Metadata = (string[])overlay.Metadata.Clone();
            }

            if (overlay.UseWeakETag.HasValue)
            {
                result.UseWeakETag = overlay.UseWeakETag;
            }

            if (overlay.CacheDuration.HasValue)
            {
                result.CacheDuration = overlay.CacheDuration;
            }

            return result;
        }
        
        public void Dispose()
        {
            _changeListener?.Dispose();
        }
    }
}
