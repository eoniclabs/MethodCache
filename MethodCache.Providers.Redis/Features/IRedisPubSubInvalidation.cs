using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    public interface IRedisPubSubInvalidation : IDisposable
    {
        Task PublishInvalidationEventAsync(string[] tags);
        Task StartListeningAsync();
        Task StopListeningAsync();
        event EventHandler<CacheInvalidationEventArgs> InvalidationReceived;
    }

    public class CacheInvalidationEventArgs : EventArgs
    {
        public string[] Tags { get; }
        public string SourceInstanceId { get; }
        public DateTimeOffset Timestamp { get; }

        public CacheInvalidationEventArgs(string[] tags, string sourceInstanceId, DateTimeOffset timestamp)
        {
            Tags = tags;
            SourceInstanceId = sourceInstanceId;
            Timestamp = timestamp;
        }
    }
}