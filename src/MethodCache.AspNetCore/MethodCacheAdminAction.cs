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
    /// <summary>
    /// Creates the description of one change, as the admin endpoints do before they make it. Construct one in a test
    /// of an audit function, with a <see cref="DefaultHttpContext"/> whose <c>User</c> is the signed-in user.
    /// </summary>
    /// <param name="kind">What the request does.</param>
    /// <param name="targets">What it changes; see <see cref="Targets"/>.</param>
    /// <param name="httpContext">The request.</param>
    /// <param name="time">When the request arrived.</param>
    /// <param name="overrideMetadata">For <see cref="MethodCacheAdminActionKind.UpsertOverride"/>, the owner, reason,
    /// ticket and expiry the override carries; otherwise <c>null</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> or <paramref name="httpContext"/> is <c>null</c>.</exception>
    public MethodCacheAdminAction(MethodCacheAdminActionKind kind, IReadOnlyList<string> targets, HttpContext httpContext, DateTimeOffset time,
        RuntimeOverrideMetadata? overrideMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(httpContext);
        Kind = kind;
        Targets = targets;
        HttpContext = httpContext;
        Time = time;
        OverrideMetadata = overrideMetadata;
    }

    /// <summary>What the request does.</summary>
    public MethodCacheAdminActionKind Kind { get; }

    /// <summary>
    /// What it changes: the stored key for <see cref="MethodCacheAdminActionKind.RemoveEntry"/>, the tags for
    /// <see cref="MethodCacheAdminActionKind.InvalidateTags"/>, the method id for
    /// <see cref="MethodCacheAdminActionKind.InvalidateMethod"/>, and nothing for <see cref="MethodCacheAdminActionKind.Clear"/>.
    /// For <see cref="MethodCacheAdminActionKind.UpsertOverride"/> and <see cref="MethodCacheAdminActionKind.RemoveOverride"/>
    /// it is the method id, <c>group:&lt;name&gt;</c> for a group, or <c>*</c> for every cached method or every override.
    /// </summary>
    public IReadOnlyList<string> Targets { get; }

    /// <summary>The signed-in user who made the request.</summary>
    public ClaimsPrincipal User => HttpContext.User;

    /// <summary>When the request arrived, from the application's <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset Time { get; }

    /// <summary>
    /// For <see cref="MethodCacheAdminActionKind.UpsertOverride"/>, the <c>Owner</c>, <c>Reason</c>, <c>Ticket</c> and
    /// <c>ExpiresAt</c> from the request body, so the audit records why the override was made; <c>null</c> for other kinds
    /// and for an override whose body gives none of them.
    /// </summary>
    public RuntimeOverrideMetadata? OverrideMetadata { get; }

    /// <summary>The request, for anything else the audit records, such as the client's address or the trace id.</summary>
    public HttpContext HttpContext { get; }
}
