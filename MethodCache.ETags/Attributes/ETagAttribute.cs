namespace MethodCache.ETags.Attributes
{
    /// <summary>
    /// Indicates that a method should use ETag-aware caching.
    /// This attribute can be used in conjunction with the MethodCache source generator
    /// to enable automatic ETag support for cached methods.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class ETagAttribute : Attribute
    {
        /// <summary>
        /// Custom ETag generation strategy.
        /// </summary>
        public ETagGenerationStrategy Strategy { get; set; } = ETagGenerationStrategy.ContentHash;

        /// <summary>
        /// Whether to include method parameters in ETag generation.
        /// Default: true
        /// </summary>
        public bool IncludeParametersInETag { get; set; } = true;

        /// <summary>
        /// Custom ETag generator type.
        /// Must implement IETagGenerator interface.
        /// </summary>
        public Type? ETagGeneratorType { get; set; }

        /// <summary>
        /// Additional metadata to include in ETag calculation.
        /// </summary>
        public string[]? Metadata { get; set; }

        /// <summary>
        /// Whether to use weak ETags (W/ prefix).
        /// Default: false (strong ETags)
        /// </summary>
        public bool UseWeakETag { get; set; } = false;

        /// <summary>
        /// Custom cache duration for ETag entries.
        /// If not specified, uses the method's cache settings.
        /// </summary>
        public int? CacheDurationMinutes { get; set; }

        /// <summary>
        /// Initializes a new instance of the ETagAttribute.
        /// </summary>
        public ETagAttribute() { }

        /// <summary>
        /// Initializes a new instance of the ETagAttribute with a specific strategy.
        /// </summary>
        /// <param name="strategy">ETag generation strategy</param>
        public ETagAttribute(ETagGenerationStrategy strategy)
        {
            Strategy = strategy;
        }
    }

    /// <summary>
    /// Defines strategies for ETag generation.
    /// </summary>
    public enum ETagGenerationStrategy
    {
        /// <summary>
        /// Generate ETag based on content hash (default).
        /// Most reliable but requires full content.
        /// </summary>
        ContentHash,

        /// <summary>
        /// Generate ETag based on last modified timestamp.
        /// Efficient but requires timestamp tracking.
        /// </summary>
        LastModified,

        /// <summary>
        /// Generate ETag based on version number.
        /// Useful for versioned resources.
        /// </summary>
        Version,

        /// <summary>
        /// Use custom ETag generator.
        /// Requires ETagGeneratorType to be specified.
        /// </summary>
        Custom
    }

    /// <summary>
    /// Interface for custom ETag generators.
    /// </summary>
    public interface IETagGenerator
    {
        /// <summary>
        /// Generates an ETag for the given content and context.
        /// </summary>
        /// <param name="content">The content to generate ETag for</param>
        /// <param name="context">Additional context for ETag generation</param>
        /// <returns>Generated ETag</returns>
        Task<string> GenerateETagAsync(object content, ETagGenerationContext context);
    }

    /// <summary>
    /// Context information for ETag generation.
    /// </summary>
    public class ETagGenerationContext
    {
        /// <summary>
        /// The method name being cached.
        /// </summary>
        public string MethodName { get; set; } = string.Empty;

        /// <summary>
        /// The method parameters.
        /// </summary>
        public object[] Parameters { get; set; } = Array.Empty<object>();

        /// <summary>
        /// Additional metadata from the ETag attribute.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// The current timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether to generate a weak ETag.
        /// </summary>
        public bool UseWeakETag { get; set; }
    }
}