namespace MethodCache.Core.Configuration.Surfaces.Attributes
{
    /// <summary>
    /// Marks a method to automatically invalidate cache entries when executed.
    /// Use this attribute on write operations (Create, Update, Delete) to keep cache consistent.
    /// </summary>
    /// <remarks>
    /// CacheInvalidateAttribute enables automatic cache invalidation based on tags.
    /// When a method decorated with this attribute executes, all cache entries with matching tags are removed.
    ///
    /// Common patterns:
    /// - Invalidate by entity type (e.g., "users", "products")
    /// - Invalidate by specific entity (e.g., "user:123")
    /// - Invalidate by feature area (e.g., "catalog", "reports")
    ///
    /// This works seamlessly with [Cache] attribute's Tags property for automatic cache management.
    /// </remarks>
    /// <example>
    /// Basic usage for write operations:
    /// <code>
    /// public interface IUserService
    /// {
    ///     [Cache(Tags = new[] { "users", "user:{userId}" })]
    ///     Task&lt;User&gt; GetUserAsync(int userId);
    ///
    ///     [CacheInvalidate(Tags = new[] { "users", "user:{userId}" })]
    ///     Task UpdateUserAsync(int userId, UserUpdateDto update);
    /// }
    /// </code>
    ///
    /// Invalidating multiple related caches:
    /// <code>
    /// [CacheInvalidate(Tags = new[] { "products", "catalog", "search-results" })]
    /// Task&lt;Product&gt; CreateProductAsync(ProductDto product);
    ///
    /// [CacheInvalidate(Tags = new[] { "products", $"product:{productId}", "catalog" })]
    /// Task DeleteProductAsync(int productId);
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CacheInvalidateAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets the tags identifying which cache entries to invalidate when this method executes.
        /// All cache entries with any of these tags will be removed from the cache.
        /// </summary>
        /// <remarks>
        /// Tag matching strategies:
        /// - Exact match: Invalidates entries with exactly matching tags
        /// - Coordinate with [Cache] attribute tags for automatic cache coherency
        /// - Use parameterized tags (e.g., "user:{userId}") for entity-specific invalidation
        ///
        /// Best practices:
        /// - Keep tags consistent between [Cache] and [CacheInvalidate] attributes
        /// - Use broad tags for full cache clears (e.g., "all-products")
        /// - Use specific tags for targeted invalidation (e.g., "product:123")
        /// </remarks>
        /// <example>
        /// <code>
        /// // Invalidate all user caches
        /// [CacheInvalidate(Tags = new[] { "users" })]
        ///
        /// // Invalidate specific user and related caches
        /// [CacheInvalidate(Tags = new[] { "users", $"user:{userId}", "user-profiles" })]
        /// </code>
        /// </example>
        public string[]? Tags { get; set; }
    }
}
