using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.HybridCache.Abstractions;
using MethodCache.Providers.Redis.Backplane;
using MethodCache.Providers.Redis.Configuration;
using System;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    /// <summary>
    /// Legacy wrapper around RedisCacheBackplane for backward compatibility.
    /// This provides the old IRedisPubSubInvalidation interface while delegating to the more comprehensive backplane.
    /// </summary>
    public class RedisPubSubInvalidation : IRedisPubSubInvalidation, IDisposable
    {
        private readonly ICacheBackplane _backplane;
        private readonly ILogger<RedisPubSubInvalidation> _logger;
        private bool _disposed;

        public event EventHandler<CacheInvalidationEventArgs>? InvalidationReceived;

        public RedisPubSubInvalidation(
            ICacheBackplane backplane,
            ILogger<RedisPubSubInvalidation> logger)
        {
            _backplane = backplane;
            _logger = logger;
            
            // Forward backplane events to our legacy event
            _backplane.InvalidationReceived += OnBackplaneInvalidationReceived;
        }

        public async Task PublishInvalidationEventAsync(string[] tags)
        {
            if (_disposed || !tags.Any())
                return;

            try
            {
                // Delegate to the more comprehensive backplane
                await _backplane.PublishInvalidationAsync(tags);
                
                _logger.LogDebug("Published invalidation event for tags {Tags} via backplane", 
                    string.Join(", ", tags));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing invalidation event for tags {Tags}", string.Join(", ", tags));
            }
        }

        public async Task StartListeningAsync()
        {
            if (_disposed)
                return;

            try
            {
                // Delegate to the backplane for listening
                await _backplane.StartListeningAsync();
                
                _logger.LogInformation("Started listening for cache invalidation events via backplane");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting pub/sub invalidation listener");
                throw; // Re-throw for consistency with original behavior
            }
        }

        public async Task StopListeningAsync()
        {
            if (_disposed)
                return;

            try
            {
                // Delegate to the backplane for stopping
                await _backplane.StopListeningAsync();
                
                _logger.LogInformation("Stopped listening for cache invalidation events via backplane");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping pub/sub invalidation listener");
            }
        }

        /// <summary>
        /// Forwards backplane invalidation events to legacy event handlers.
        /// Converts the comprehensive backplane event format to the legacy tags-only format.
        /// </summary>
        private void OnBackplaneInvalidationReceived(object? sender, MethodCache.HybridCache.Abstractions.CacheInvalidationEventArgs e)
        {
            try
            {
                // Convert from comprehensive backplane format to legacy tags-only format
                var legacyEventArgs = new Features.CacheInvalidationEventArgs(
                    e.Tags,
                    e.SourceInstanceId,
                    new DateTimeOffset(e.Timestamp));

                InvalidationReceived?.Invoke(this, legacyEventArgs);

                _logger.LogDebug("Forwarded backplane invalidation event for tags {Tags} from instance {SourceInstance}", 
                    string.Join(", ", e.Tags), e.SourceInstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error forwarding backplane invalidation event");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                // Unregister from backplane events
                _backplane.InvalidationReceived -= OnBackplaneInvalidationReceived;
                
                // Use timeout to prevent deadlock during synchronous disposal
                var stopTask = StopListeningAsync();
                if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    _logger.LogWarning("StopListeningAsync timed out during disposal, forcing cleanup");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during disposal of pub/sub invalidation service");
            }
            finally
            {
                _disposed = true;
            }
        }

    }
}