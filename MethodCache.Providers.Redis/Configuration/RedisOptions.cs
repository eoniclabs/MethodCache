using System;

namespace MethodCache.Providers.Redis.Configuration
{
    public class RedisOptions
    {
        public string ConnectionString { get; set; } = Environment.GetEnvironmentVariable("MethodCache_Redis_ConnectionString") ?? "localhost:6379";
        public int DatabaseNumber { get; set; } = 0;
        public string KeyPrefix { get; set; } = "methodcache:";
        public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(30);
        
        // Connection Management
        public int MaxConnections { get; set; } = 100;
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(5);
        
        // Serialization
        public RedisSerializerType DefaultSerializer { get; set; } = RedisSerializerType.MessagePack;
        public RedisCompressionType Compression { get; set; } = RedisCompressionType.None;
        public int CompressionThreshold { get; set; } = 1024;
        
        // Resilience
        public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
        public RetryOptions Retry { get; set; } = new();
        
        // Advanced Features
        public bool EnableDistributedLocking { get; set; } = true;
        public bool EnablePubSubInvalidation { get; set; } = false;
        public bool EnableCacheWarming { get; set; } = false;
        
        // Cross-Instance Communication
        public string BackplaneChannel { get; set; } = "methodcache-shared";
        public BackplaneSerializerType BackplaneSerializer { get; set; } = BackplaneSerializerType.Json;
        
        // Monitoring
        public bool EnableDetailedMetrics { get; set; } = true;
        public bool EnableSlowLogMonitoring { get; set; } = false;
    }

    public enum BackplaneSerializerType
    {
        Json,
        MessagePack
    }

    public class CircuitBreakerOptions
    {
        public double FailureRatio { get; set; } = 0.5;
        public int MinimumThroughput { get; set; } = 10;
        public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
    }

    public class RetryOptions
    {
        public int MaxRetries { get; set; } = 3;
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);
        public RetryBackoffType BackoffType { get; set; } = RetryBackoffType.Exponential;
    }

    public enum RedisSerializerType
    {
        MessagePack,
        Json,
        Binary
    }

    public enum RedisCompressionType
    {
        None,
        Gzip,
        Brotli
    }

    public enum RetryBackoffType
    {
        Linear,
        Exponential,
        ExponentialWithJitter
    }
}