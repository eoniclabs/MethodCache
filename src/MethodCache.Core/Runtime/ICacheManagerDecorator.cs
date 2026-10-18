namespace MethodCache
{
    /// <summary>
    /// Implemented by an <see cref="ICacheManager"/> that wraps another one, such as the OpenTelemetry
    /// instrumentation, so MethodCache can reach the wrapped manager's features that <see cref="ICacheManager"/>
    /// does not expose.
    /// </summary>
    /// <remarks>
    /// <c>GetOrCreateManyAsync</c> uses it to read the distributed cache for the keys missing from memory before it
    /// runs the bulk factory. Implement it on your own decorator so bulk reads keep doing that through it.
    /// </remarks>
    internal interface ICacheManagerDecorator
    {
        /// <summary>The cache manager this one wraps.</summary>
        ICacheManager InnerManager { get; }
    }
}
