namespace MethodCache
{
    /// <summary>
    /// Removes the cached entries with any of <see cref="Tags"/> after this interface method has completed
    /// successfully. Put it on the methods that change data (create, update, delete).
    /// </summary>
    /// <remarks>
    /// <para>The entries are removed after the method returns, or after its <c>Task</c> or <c>ValueTask</c> completes,
    /// and only when it succeeds: if it throws, nothing is removed. This instance's memory is cleared before the call
    /// returns to the caller; with a distributed cache, other instances drop their copies when the invalidation message
    /// reaches them.</para>
    /// <para>When the data changes somewhere else, such as an endpoint that writes to the database directly or a
    /// transaction that commits after this method returns, invalidate after the write or commit with
    /// <c>ICacheManager.InvalidateTagAsync</c> and a tag from <see cref="IMethodCacheKeys.Tag{TService}"/>, or a key from
    /// <see cref="IMethodCacheKeys.For{TService}"/>.</para>
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
    /// [CacheInvalidate(Tags = new[] { "products", "product:{productId}", "catalog" })]
    /// Task DeleteProductAsync(int productId);
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class CacheInvalidateAttribute : Attribute
    {
        /// <summary>
        /// The tags of the entries to remove once the method has completed successfully. An entry with any of them is
        /// removed. Tags match exactly; there are no wildcards.
        /// </summary>
        /// <remarks>
        /// A template such as <c>"user:{userId}"</c> is filled in from this method's parameter with that name, written the
        /// same way as on the cached entries (see <see cref="CacheAttribute.Tags"/>), so <c>UpdateUserAsync(42, ...)</c>
        /// removes the entries tagged <c>user:42</c>.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Invalidate all user caches
        /// [CacheInvalidate(Tags = new[] { "users" })]
        ///
        /// // Invalidate specific user and related caches
        /// [CacheInvalidate(Tags = new[] { "users", "user:{userId}", "user-profiles" })]
        /// </code>
        /// </example>
        public string[]? Tags { get; set; }
    }
}
