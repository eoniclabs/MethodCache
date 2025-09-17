using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Providers.Redis.Configuration;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Services
{
    /// <summary>
    /// Hosted service that initializes Redis connection asynchronously during application startup
    /// to prevent blocking the application startup process.
    /// </summary>
    public class RedisConnectionService : IHostedService, IAsyncDisposable
    {
        private readonly RedisOptions _options;
        private readonly ILogger<RedisConnectionService> _logger;
        private IConnectionMultiplexer? _connectionMultiplexer;
        private readonly TaskCompletionSource<IConnectionMultiplexer> _connectionTcs;
        
        public RedisConnectionService(
            IOptions<RedisOptions> options, 
            ILogger<RedisConnectionService> logger)
        {
            _options = options.Value;
            _logger = logger;
            _connectionTcs = new TaskCompletionSource<IConnectionMultiplexer>();
        }

        /// <summary>
        /// Gets the Redis connection, waiting for it to be initialized if necessary.
        /// </summary>
        public Task<IConnectionMultiplexer> GetConnectionAsync()
        {
            return _connectionTcs.Task;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Initializing Redis connection asynchronously...");
                
                var config = ConfigurationOptions.Parse(_options.ConnectionString);
                
                // Apply all configuration options that were previously unused
                config.ConnectTimeout = (int)_options.ConnectTimeout.TotalMilliseconds;
                config.SyncTimeout = (int)_options.SyncTimeout.TotalMilliseconds;
                
                // Note: MaxConnections is not directly configurable in StackExchange.Redis ConfigurationOptions
                // It's handled by the connection multiplexer internally
                
                // Add slow log monitoring if enabled
                if (_options.EnableSlowLogMonitoring)
                {
                    config.IncludeDetailInExceptions = true;
                    config.IncludePerformanceCountersInExceptions = true;
                }
                
                // Enable detailed metrics if configured
                if (_options.EnableDetailedMetrics)
                {
                    config.IncludePerformanceCountersInExceptions = true;
                }

                // Apply retry configuration to connection
                config.ConnectRetry = _options.Retry.MaxRetries;
                config.ReconnectRetryPolicy = new ExponentialRetry((int)_options.Retry.BaseDelay.TotalMilliseconds);
                
                // Use async connection to prevent blocking startup
                _connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(config);
                
                // Set up connection event handlers
                _connectionMultiplexer.ConnectionFailed += OnConnectionFailed;
                _connectionMultiplexer.ConnectionRestored += OnConnectionRestored;
                _connectionMultiplexer.ErrorMessage += OnErrorMessage;
                
                if (_options.EnableSlowLogMonitoring)
                {
                    // Set up slow log monitoring
                    _ = Task.Run(() => MonitorSlowLogAsync(cancellationToken), cancellationToken);
                }
                
                _connectionTcs.SetResult(_connectionMultiplexer);
                _logger.LogInformation("Redis connection initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Redis connection");
                _connectionTcs.SetException(ex);
                throw; // Re-throw to fail the hosted service startup
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_connectionMultiplexer != null)
            {
                _logger.LogInformation("Closing Redis connection...");
                
                // Unregister event handlers
                _connectionMultiplexer.ConnectionFailed -= OnConnectionFailed;
                _connectionMultiplexer.ConnectionRestored -= OnConnectionRestored;
                _connectionMultiplexer.ErrorMessage -= OnErrorMessage;
                
                await _connectionMultiplexer.DisposeAsync();
                _logger.LogInformation("Redis connection closed");
            }
        }

        private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs e)
        {
            _logger.LogWarning("Redis connection failed: {Exception} - {FailureType}", 
                e.Exception?.Message, e.FailureType);
        }

        private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e)
        {
            _logger.LogInformation("Redis connection restored: {FailureType}", e.FailureType);
        }

        private void OnErrorMessage(object? sender, RedisErrorEventArgs e)
        {
            _logger.LogError("Redis error: {Message}", e.Message);
        }

        private async Task MonitorSlowLogAsync(CancellationToken cancellationToken)
        {
            if (_connectionMultiplexer == null) return;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var server = _connectionMultiplexer.GetServer(_connectionMultiplexer.GetEndPoints()[0]);
                        var slowLogEntries = await server.SlowlogGetAsync(10);
                        
                        foreach (var entry in slowLogEntries)
                        {
                            if (entry.Duration.TotalMilliseconds > 100) // Log operations over 100ms
                            {
                                _logger.LogWarning("Slow Redis operation detected: {Command} took {Duration}ms", 
                                    string.Join(" ", entry.Arguments), entry.Duration.TotalMilliseconds);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error monitoring Redis slow log (this is expected if not admin)");
                    }

                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in slow log monitoring");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_connectionMultiplexer != null)
            {
                await _connectionMultiplexer.DisposeAsync();
            }
        }
    }
}