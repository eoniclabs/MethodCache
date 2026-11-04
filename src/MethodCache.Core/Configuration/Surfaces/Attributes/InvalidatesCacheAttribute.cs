namespace MethodCache
{
    /// <summary>
    /// Marks a method of your own that invalidates cached entries, such as a helper around
    /// <c>ICacheManager.InvalidateTagsAsync</c>. It changes nothing at run time: the analyzers treat a call to the
    /// method as an invalidation, so MC0027 doesn't report a write next to the call or inside the method.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CacheInvalidateAttribute"/>, which makes the generated cache invalidate tags after an
    /// interface method runs, this attribute only says that the method does the invalidation itself.
    /// </remarks>
    /// <example>
    /// <code>
    /// public static class CacheChanges
    /// {
    ///     [InvalidatesCache]
    ///     public static void Changed(this HttpContext http, string tag) =&gt; ...;
    /// }
    ///
    /// await db.Publications.Where(p =&gt; p.Id == id).ExecuteUpdateAsync(s =&gt; s.SetProperty(p =&gt; p.Revoked, true));
    /// HttpContext.Changed("publications");
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class InvalidatesCacheAttribute : Attribute
    {
    }
}
