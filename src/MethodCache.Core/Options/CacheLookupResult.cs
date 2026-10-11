namespace MethodCache.Core.Options
{
    /// <summary>
    /// Represents the outcome of a cache lookup without materialising allocations.
    /// </summary>
    public readonly struct CacheLookupResult<T>
    {
        private CacheLookupResult(bool found, T? value)
        {
            Found = found;
            Value = value;
        }

        /// <summary>
        /// Indicates whether the value was present in the cache.
        /// </summary>
        public bool Found { get; }

        /// <summary>
        /// The cached value when <see cref="Found"/> is <c>true</c>; otherwise default.
        /// </summary>
        public T? Value { get; }

        /// <summary>
        /// Creates a hit result with the provided value.
        /// </summary>
        public static CacheLookupResult<T> Hit(T value) => new(true, value);

        /// <summary>
        /// Represents a miss result.
        /// </summary>
        public static CacheLookupResult<T> Miss => new(false, default);
    }
}
