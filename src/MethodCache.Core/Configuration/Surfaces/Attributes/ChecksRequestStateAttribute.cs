namespace MethodCache
{
    /// <summary>
    /// Marks a method of your own that checks a value against the current request and throws when they differ, such as
    /// <c>EnsureActsFor(tenantId)</c> on a tenant context. It changes nothing at run time. A cached method that passes
    /// one of its parameters to the check loads its result only for that value's own request, so analyzer MC0026
    /// doesn't report a class whose cached methods all do.
    /// </summary>
    /// <remarks>
    /// The check runs when the cached method loads a value, not on a hit: a caller that passes another tenant's id gets
    /// that tenant's cached result without the check. Take the value from the request, or authorize it, before the call,
    /// as for every cached method that takes a tenant or user id. Refresh-ahead loads run without a request, so the
    /// check fails in them and the entry is loaded again on the next call instead. The analyzer reads the attribute on
    /// the method that is called: when the check is called through an interface, mark the interface method.
    /// </remarks>
    /// <example>
    /// <code>
    /// public sealed class TenantDb(...) : DbContext(...)
    /// {
    ///     [ChecksRequestState]
    ///     public void EnsureActsFor(Guid tenantId) { if (tenantId != _tenant.Id) throw new UnauthorizedAccessException(); }
    /// }
    ///
    /// public sealed class Projects(TenantDb db) : IProjects
    /// {
    ///     public Task&lt;List&lt;Project&gt;&gt; ListAsync(Guid tenantId)
    ///     {
    ///         db.EnsureActsFor(tenantId);
    ///         return db.Projects.ToListAsync();
    ///     }
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ChecksRequestStateAttribute : Attribute
    {
    }
}
