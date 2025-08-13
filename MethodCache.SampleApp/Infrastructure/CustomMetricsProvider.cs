using MethodCache.Core;
using System.Collections.Concurrent;

namespace MethodCache.SampleApp.Infrastructure
{
    /// <summary>
    /// Enhanced metrics provider that tracks detailed cache statistics
    /// </summary>
    public class EnhancedMetricsProvider : ICacheMetricsProvider
    {
        private readonly ConcurrentDictionary<string, MethodMetrics> _methodMetrics = new();
        private readonly object _lockObject = new();
        private readonly Timer _reportingTimer;

        public EnhancedMetricsProvider()
        {
            // Report metrics every 30 seconds
            _reportingTimer = new Timer(ReportMetrics, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public void CacheHit(string methodName)
        {
            var metrics = _methodMetrics.GetOrAdd(methodName, _ => new MethodMetrics { MethodName = methodName });
            Interlocked.Increment(ref metrics.HitCount);
            Interlocked.Exchange(ref metrics.LastHitTime, DateTime.UtcNow.Ticks);
        }

        public void CacheMiss(string methodName)
        {
            var metrics = _methodMetrics.GetOrAdd(methodName, _ => new MethodMetrics { MethodName = methodName });
            Interlocked.Increment(ref metrics.MissCount);
            Interlocked.Exchange(ref metrics.LastMissTime, DateTime.UtcNow.Ticks);
        }

        public void CacheError(string methodName, string errorMessage)
        {
            var metrics = _methodMetrics.GetOrAdd(methodName, _ => new MethodMetrics { MethodName = methodName });
            Interlocked.Increment(ref metrics.ErrorCount);
            Interlocked.Exchange(ref metrics.LastErrorTime, DateTime.UtcNow.Ticks);
            
            // Add error to recent errors list (thread-safe)
            lock (metrics.RecentErrors)
            {
                metrics.RecentErrors.Add(new ErrorInfo
                {
                    Message = errorMessage,
                    Timestamp = DateTime.UtcNow
                });
                
                // Keep only last 10 errors
                if (metrics.RecentErrors.Count > 10)
                {
                    metrics.RecentErrors.RemoveAt(0);
                }
            }

            Console.WriteLine($"[CACHE ERROR] {methodName}: {errorMessage}");
        }

        public void CacheLatency(string methodName, long elapsedMilliseconds)
        {
            var metrics = _methodMetrics.GetOrAdd(methodName, _ => new MethodMetrics { MethodName = methodName });
            
            lock (_lockObject)
            {
                metrics.TotalLatencyMs += elapsedMilliseconds;
                metrics.LatencyCount++;
                
                if (elapsedMilliseconds > metrics.MaxLatencyMs)
                    metrics.MaxLatencyMs = elapsedMilliseconds;
                
                if (metrics.MinLatencyMs == 0 || elapsedMilliseconds < metrics.MinLatencyMs)
                    metrics.MinLatencyMs = elapsedMilliseconds;
            }
        }

        public CacheMetricsSummary GetMetricsSummary()
        {
            var summary = new CacheMetricsSummary
            {
                GeneratedAt = DateTime.UtcNow,
                MethodMetrics = new List<MethodMetrics>()
            };

            foreach (var kvp in _methodMetrics)
            {
                var metrics = kvp.Value;
                var clonedMetrics = new MethodMetrics
                {
                    MethodName = metrics.MethodName,
                    HitCount = metrics.HitCount,
                    MissCount = metrics.MissCount,
                    ErrorCount = metrics.ErrorCount,
                    TotalLatencyMs = metrics.TotalLatencyMs,
                    LatencyCount = metrics.LatencyCount,
                    MaxLatencyMs = metrics.MaxLatencyMs,
                    MinLatencyMs = metrics.MinLatencyMs,
                    LastHitTime = metrics.LastHitTime,
                    LastMissTime = metrics.LastMissTime,
                    LastErrorTime = metrics.LastErrorTime
                };

                // Clone recent errors safely
                lock (metrics.RecentErrors)
                {
                    clonedMetrics.RecentErrors = new List<ErrorInfo>(metrics.RecentErrors);
                }

                summary.MethodMetrics.Add(clonedMetrics);
            }

            // Calculate aggregate metrics
            summary.TotalHits = summary.MethodMetrics.Sum(m => m.HitCount);
            summary.TotalMisses = summary.MethodMetrics.Sum(m => m.MissCount);
            summary.TotalErrors = summary.MethodMetrics.Sum(m => m.ErrorCount);
            summary.OverallHitRatio = summary.TotalHits + summary.TotalMisses > 0 
                ? (double)summary.TotalHits / (summary.TotalHits + summary.TotalMisses) 
                : 0;

            return summary;
        }

        public void ResetMetrics()
        {
            _methodMetrics.Clear();
            Console.WriteLine("[METRICS] All metrics have been reset");
        }

        private void ReportMetrics(object? state)
        {
            if (_methodMetrics.IsEmpty)
                return;

            Console.WriteLine("\n=== CACHE METRICS REPORT ===");
            Console.WriteLine($"Report Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine();

            var summary = GetMetricsSummary();
            
            Console.WriteLine($"Overall Statistics:");
            Console.WriteLine($"  Total Hits: {summary.TotalHits:N0}");
            Console.WriteLine($"  Total Misses: {summary.TotalMisses:N0}");
            Console.WriteLine($"  Total Errors: {summary.TotalErrors:N0}");
            Console.WriteLine($"  Hit Ratio: {summary.OverallHitRatio:P2}");
            Console.WriteLine();

            Console.WriteLine("Method-specific Metrics:");
            foreach (var metrics in summary.MethodMetrics.OrderByDescending(m => m.HitCount + m.MissCount))
            {
                var totalRequests = metrics.HitCount + metrics.MissCount;
                var hitRatio = totalRequests > 0 ? (double)metrics.HitCount / totalRequests : 0;
                var avgLatency = metrics.LatencyCount > 0 ? metrics.TotalLatencyMs / (double)metrics.LatencyCount : 0;

                Console.WriteLine($"  {metrics.MethodName}:");
                Console.WriteLine($"    Hits/Misses: {metrics.HitCount}/{metrics.MissCount} (Ratio: {hitRatio:P1})");
                Console.WriteLine($"    Errors: {metrics.ErrorCount}");
                Console.WriteLine($"    Avg Latency: {avgLatency:F1}ms (Min: {metrics.MinLatencyMs}ms, Max: {metrics.MaxLatencyMs}ms)");
                
                if (metrics.RecentErrors.Any())
                {
                    Console.WriteLine($"    Recent Errors: {metrics.RecentErrors.Count}");
                }
                Console.WriteLine();
            }

            Console.WriteLine("===============================\n");
        }

        public void Dispose()
        {
            _reportingTimer?.Dispose();
        }
    }

    public class MethodMetrics
    {
        public string MethodName { get; set; } = string.Empty;
        public long HitCount;
        public long MissCount;
        public long ErrorCount;
        public long TotalLatencyMs;
        public long LatencyCount;
        public long MaxLatencyMs;
        public long MinLatencyMs;
        public long LastHitTime;
        public long LastMissTime;
        public long LastErrorTime;
        public List<ErrorInfo> RecentErrors { get; set; } = new();

        public double HitRatio => HitCount + MissCount > 0 ? (double)HitCount / (HitCount + MissCount) : 0;
        public double AverageLatencyMs => LatencyCount > 0 ? TotalLatencyMs / (double)LatencyCount : 0;
        public DateTime? LastHitTimeUtc => LastHitTime > 0 ? new DateTime(LastHitTime, DateTimeKind.Utc) : null;
        public DateTime? LastMissTimeUtc => LastMissTime > 0 ? new DateTime(LastMissTime, DateTimeKind.Utc) : null;
        public DateTime? LastErrorTimeUtc => LastErrorTime > 0 ? new DateTime(LastErrorTime, DateTimeKind.Utc) : null;
    }

    public class ErrorInfo
    {
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    public class CacheMetricsSummary
    {
        public DateTime GeneratedAt { get; set; }
        public long TotalHits { get; set; }
        public long TotalMisses { get; set; }
        public long TotalErrors { get; set; }
        public double OverallHitRatio { get; set; }
        public List<MethodMetrics> MethodMetrics { get; set; } = new();
    }
}