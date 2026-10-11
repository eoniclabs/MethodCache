namespace MethodCache
{
    /// <summary>
    /// Lets a type used as a cached method argument say what it contributes to the cache key. Implement it on types
    /// whose state is private, that cannot be serialized, or when running under Native AOT.
    /// </summary>
    public interface ICacheKeyProvider
    {
        /// <summary>
        /// The text this value contributes to a cache key. Equal values must return the same text and different values
        /// different text.
        /// </summary>
        string CacheKeyPart { get; }
    }
}
