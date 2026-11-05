namespace MethodCache.Core.Management;

/// <summary>
/// Tags that an integration invalidates by itself, such as the entity tags of MethodCache.EntityFrameworkCore, for
/// MethodCache.Testing's <c>TagCoverage</c>. Each integration registers one per configuration it owns.
/// </summary>
internal interface IInvalidatedTagSource
{
    /// <summary>The tag templates, such as <c>"product:{Id}"</c>; a placeholder matches any value.</summary>
    IReadOnlyList<string> InvalidatedTags { get; }
}
