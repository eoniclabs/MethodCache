using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace MethodCache;

/// <summary>What a request to an admin endpoint that changes the cache does.</summary>
public enum MethodCacheAdminActionKind
{
    /// <summary><c>DELETE entry</c>: removes one entry, by its stored key.</summary>
    RemoveEntry,

    /// <summary><c>POST invalidate</c>: removes the entries with any of the tags.</summary>
    InvalidateTags,

    /// <summary><c>POST invalidate-method</c>: removes every entry of one cached method.</summary>
    InvalidateMethod,

    /// <summary><c>POST clear</c>: removes every entry.</summary>
    Clear,

    /// <summary>
    /// <c>PUT overrides</c>: sets a runtime override. Its target is the method id, <c>group:&lt;name&gt;</c> for a group or
    /// <c>*</c> for every cached method.
    /// </summary>
    UpsertOverride,

    /// <summary>
    /// <c>DELETE overrides</c>: removes runtime overrides. Its target is the method id, <c>group:&lt;name&gt;</c> for a
    /// group or <c>*</c> for every override.
    /// </summary>
    RemoveOverride,
}

/// <summary>
/// A change to the cache through the admin endpoints, as the audit function of <c>MapMethodCacheAdmin</c> gets it:
/// who asked, what for, which entries, and when.
/// </summary>
public sealed class MethodCacheAdminAction
{
    internal MethodCacheAdminAction(MethodCacheAdminActionKind kind, IReadOnlyList<string> targets, HttpContext httpContext, DateTimeOffset time)
    {
        Kind = kind;
        Targets = targets;
        HttpContext = httpContext;
        Time = time;
    }

    /// <summary>What the request does.</summary>
    public MethodCacheAdminActionKind Kind { get; }

    /// <summary>
    /// What it removes: the stored key for <see cref="MethodCacheAdminActionKind.RemoveEntry"/>, the tags for
    /// <see cref="MethodCacheAdminActionKind.InvalidateTags"/>, the method id for
    /// <see cref="MethodCacheAdminActionKind.InvalidateMethod"/>, and nothing for <see cref="MethodCacheAdminActionKind.Clear"/>.
    /// </summary>
    public IReadOnlyList<string> Targets { get; }

    /// <summary>The signed-in user who made the request.</summary>
    public ClaimsPrincipal User => HttpContext.User;

    /// <summary>When the request arrived, from the application's <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset Time { get; }

    /// <summary>The request, for anything else the audit records, such as the client's address or the trace id.</summary>
    public HttpContext HttpContext { get; }
}
