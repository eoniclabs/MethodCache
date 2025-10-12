namespace MethodCache.Core.Runtime.Core
{
    /// <summary>
    /// Context passed to fluent cache factories, exposing the cache key and optional services.
    /// </summary>
    public sealed class CacheContext
    {
        internal CacheContext(string key, IServiceProvider? services)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Services = services;
            Items = new Dictionary<object, object?>();
        }

        /// <summary>
        /// Gets the cache key associated with the current operation.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Gets the scoped service provider, when available.
        /// </summary>
        public IServiceProvider? Services { get; }

        /// <summary>
        /// Gets a dictionary for sharing state during cache factory execution.
        /// </summary>
        public IDictionary<object, object?> Items { get; }
    }
}
