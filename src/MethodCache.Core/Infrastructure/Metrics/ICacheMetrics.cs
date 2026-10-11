namespace MethodCache.Core.Infrastructure.Metrics
{
    public interface ICacheMetrics
    {
        void RecordHit(string key, TimeSpan? duration, object? value);
        void RecordMiss(string key);
        void RecordError(string key, Exception exception);
    }
}
