namespace MethodCache
{
    /// <summary>
    /// Context passed to cache factories, exposing the logical key and per-invocation items.
    /// </summary>
    public sealed class CacheContext
    {
        /// <summary>Creates a factory context for the logical key.</summary>
        public CacheContext(string key)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Items = new Dictionary<object, object?>();
        }

        /// <summary>
        /// Gets the cache key associated with the current operation.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Gets a dictionary for sharing state during cache factory execution.
        /// </summary>
        public IDictionary<object, object?> Items { get; }
    }
}
