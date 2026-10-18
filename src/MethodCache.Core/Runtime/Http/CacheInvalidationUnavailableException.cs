namespace MethodCache.Core.Runtime.Http;

// Internal transport outcome; callers must not mistake locally queued invalidation for confirmation.
internal sealed class CacheInvalidationUnavailableException(string message) : InvalidOperationException(message);
