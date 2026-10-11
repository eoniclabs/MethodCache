namespace MethodCache.Extensibility
{
    /// <summary>
    /// Observes a single <c>GetOrCreateAsync</c> call, attached with <see cref="CacheEntryOptions.Builder.WithMetrics"/>.
    /// </summary>
    public interface ICacheMetrics
    {
        /// <summary>Called when the value was served from the cache.</summary>
        /// <param name="key">The key passed to <c>GetOrCreateAsync</c>.</param>
        /// <param name="duration">How long the call took.</param>
        /// <param name="value">The cached value that was returned.</param>
        void RecordHit(string key, TimeSpan? duration, object? value);
        /// <summary>Called after the factory ran and returned a value, including calls that skipped the cache.</summary>
        /// <param name="key">The key passed to <c>GetOrCreateAsync</c>.</param>
        void RecordMiss(string key);
        /// <summary>Called when the factory threw; the exception is then rethrown to the caller.</summary>
        /// <param name="key">The key passed to <c>GetOrCreateAsync</c>.</param>
        /// <param name="exception">The exception the factory threw.</param>
        void RecordError(string key, Exception exception);
    }
}
