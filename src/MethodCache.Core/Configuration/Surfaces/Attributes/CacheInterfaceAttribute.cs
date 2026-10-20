
namespace MethodCache
{
    /// <summary>
    /// Caches methods of an interface you don't own, such as a client from a NuGet package, without writing a
    /// wrapper. The listed methods behave as if they had <see cref="CacheAttribute"/> with these settings, and
    /// <c>AddMethodCache()</c> wraps the interface's registration like any other cached service.
    /// </summary>
    /// <remarks>
    /// Only the methods named in <see cref="Methods"/> are cached, so a call that changes data is never cached by
    /// accident; every other method calls the implementation directly. Settings for one method can be changed like
    /// those of any cached method: with <c>mc.Method(...)</c>, <c>appsettings.json</c> (method id: the name of the
    /// assembly that declares the interface in brackets, the interface's full name, a dot and the method name, such as
    /// <c>[Fx.Sdk]Fx.Sdk.IFxClient.GetRateAsync</c>) or <c>IRuntimeCacheConfigurator</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// [assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync), Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]
    ///
    /// builder.Services.AddSingleton&lt;IFxClient, FxClient&gt;();
    /// builder.Services.AddMethodCache();
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class CacheInterfaceAttribute : Attribute
    {
        /// <summary>Caches <paramref name="methods"/> of <paramref name="interfaceType"/>.</summary>
        /// <param name="interfaceType">The interface to cache, for example <c>typeof(IFxClient)</c>.</param>
        /// <param name="methods">The names of the methods to cache; use <c>nameof</c>.</param>
        public CacheInterfaceAttribute(Type interfaceType, params string[] methods)
        {
            InterfaceType = interfaceType;
            Methods = methods;
        }

        /// <summary>Gets the interface whose methods are cached.</summary>
        public Type InterfaceType { get; }

        /// <summary>Gets the names of the cached methods.</summary>
        public string[] Methods { get; }

        /// <summary>Gets or sets the group the methods join, as <see cref="CacheAttribute.Group"/>.</summary>
        public string? Group { get; set; }

        /// <summary>Gets or sets the cache duration, as <see cref="CacheAttribute.Duration"/>.</summary>
        public string? Duration { get; set; }

        /// <summary>Gets or sets a sliding expiration, as <see cref="CacheAttribute.SlidingExpiration"/>.</summary>
        public string? SlidingExpiration { get; set; }

        /// <summary>Gets or sets a refresh-ahead window, as <see cref="CacheAttribute.RefreshAhead"/>.</summary>
        public string? RefreshAhead { get; set; }

        /// <summary>Gets or sets the fail-safe duration or <c>"off"</c>, as <see cref="CacheAttribute.FailSafe"/>.</summary>
        public string? FailSafe { get; set; }

        /// <summary>Gets or sets the fail-safe timeout, as <see cref="CacheAttribute.FailSafeTimeout"/>.</summary>
        public string? FailSafeTimeout { get; set; }

        /// <summary>Gets or sets how long a served fail-safe value is reused before the method is called again, for example "30s".</summary>
        public string? FailSafeRetryAfter { get; set; }

        /// <summary>Gets or sets whether caching is enabled. Omission inherits the configured policy.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets whether the method uses distributed caching. Omission inherits the configured policy.</summary>
        public bool Distributed { get; set; } = true;

        /// <summary>
        /// Gets or sets tags for the cached entries, as <see cref="CacheAttribute.Tags"/>. Templates such as
        /// <c>"fx:{from}"</c> use the method's parameter names.
        /// </summary>
        public string[]? Tags { get; set; }

        /// <summary>Gets or sets the cache version, as <see cref="CacheAttribute.Version"/>.</summary>
        public int Version { get; set; }
    }
}
