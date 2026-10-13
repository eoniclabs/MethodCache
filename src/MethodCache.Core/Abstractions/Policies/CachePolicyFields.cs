using System;

namespace MethodCache.Policies;

/// <summary>
/// Flags naming the fields of a <see cref="CachePolicy"/>, used to record which fields a source set or cleared.
/// </summary>
[Flags]
public enum CachePolicyFields
{
    /// <summary>
    /// No fields.
    /// </summary>
    None = 0,
    /// <summary>
    /// The <see cref="CachePolicy.Duration"/> field.
    /// </summary>
    Duration = 1 << 0,
    /// <summary>
    /// The <see cref="CachePolicy.Tags"/> field.
    /// </summary>
    Tags = 1 << 1,
    /// <summary>
    /// The <see cref="CachePolicy.SlidingExpiration"/> field.
    /// </summary>
    SlidingExpiration = 1 << 2,
    /// <summary>
    /// The <see cref="CachePolicy.Version"/> field.
    /// </summary>
    Version = 1 << 3,
    /// <summary>
    /// The <see cref="CachePolicy.Metadata"/> field.
    /// </summary>
    Metadata = 1 << 4,
    /// <summary>
    /// The <see cref="CachePolicy.RefreshAhead"/> field.
    /// </summary>
    RefreshAhead = 1 << 5,
    /// <summary>
    /// The <see cref="CachePolicy.CacheWhen"/> field.
    /// </summary>
    CacheWhen = 1 << 6,
    /// <summary>
    /// The <see cref="CachePolicy.FailSafe"/> field.
    /// </summary>
    FailSafe = 1 << 7,
    /// <summary>
    /// The <see cref="CachePolicy.FailSafeTimeout"/> field.
    /// </summary>
    FailSafeTimeout = 1 << 8,
    /// <summary>
    /// The <see cref="CachePolicy.FailSafeRetryAfter"/> field.
    /// </summary>
    FailSafeRetryAfter = 1 << 9,
    /// <summary>
    /// This layer's <see cref="CachePolicy.Tags"/> replace the tags of the layers below it (the <c>[Cache]</c>
    /// attribute, other sources and the defaults). Without this flag, the tags of every layer are combined.
    /// </summary>
    ClearTags = 1 << 10
}
