using MethodCache;
using MethodCache.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps HTTP endpoints for inspecting and changing the cache at runtime.</summary>
public static class MethodCacheEndpointRouteBuilderExtensions
{
    private const int MaxListLimit = 1000;

    /// <summary>
    /// Maps the MethodCache admin endpoints under <paramref name="prefix"/>:
    /// <list type="bullet">
    /// <item><c>GET stats</c>: hit, miss, eviction, entry and memory statistics</item>
    /// <item><c>GET methods</c>: the effective policy of every cached method</item>
    /// <item><c>GET entries?storedKeyPrefix=&amp;tag=&amp;methodId=&amp;limit=</c>: live entries, without their values</item>
    /// <item><c>GET entry?storedKey=</c> and <c>DELETE entry?storedKey=</c>: one entry, by the <c>storedKey</c> that
    /// <c>entries</c> lists</item>
    /// <item><c>POST invalidate</c> with <c>{ "tags": [...] }</c>: remove entries by tag</item>
    /// <item><c>POST invalidate-method</c> with <c>{ "methodId": "..." }</c>: remove every entry of one cached method,
    /// by the id <c>methods</c> lists</item>
    /// <item><c>POST clear</c> with <c>{}</c>: remove every entry</item>
    /// </list>
    /// The endpoints expose cache keys and can empty the cache, so every request must satisfy the authorization
    /// policy <paramref name="policyName"/>. A signed-in user is not enough on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A query parameter an endpoint does not take, a missing or empty <c>storedKey</c> or <c>methodId</c>, a
    /// <c>methodId</c> that no cached method has, an out-of-range <c>limit</c> or a malformed body is answered with 400
    /// and changes nothing. The <c>POST</c> endpoints take only an
    /// <c>application/json</c> body (415 otherwise), which a browser does not send cross-site without a CORS preflight.
    /// </para>
    /// <para>
    /// When a removal has completed locally but its distributed confirmation is still pending, the endpoint answers
    /// 503; the cache retains the operation and retries it.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireRole("Admin")));
    /// app.MapMethodCacheAdmin("CacheAdmins");
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapMethodCacheAdmin(this IEndpointRouteBuilder endpoints, string policyName, string prefix = "/methodcache")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = endpoints.MapGroup(prefix);
        group.RequireAuthorization(policyName);
        group.WithTags("MethodCache");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context).ConfigureAwait(false); }
            catch (CacheInvalidationQueuedException)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Cache invalidation is awaiting distributed confirmation.",
                    detail: "Local invalidation has completed. The cache retains the operation for recovery.");
            }
        });

        // Each endpoint names the query parameters it takes; any other is rejected, so a misspelt or renamed filter
        // is an error rather than silently ignored
        group.MapGet("/stats", GetStatsAsync).AcceptsQuery();
        group.MapGet("/methods", (ICacheAdministrator admin) => TypedResults.Ok(admin.GetMethods())).AcceptsQuery();
        group.MapGet("/entries", ListEntries).AcceptsQuery("storedKeyPrefix", "tag", "methodId", "limit");
        group.MapGet("/entry", GetEntry).AcceptsQuery("storedKey");
        group.MapDelete("/entry", RemoveEntryAsync).AcceptsQuery("storedKey");
        group.MapPost("/invalidate", InvalidateAsync).AcceptsQuery();
        group.MapPost("/invalidate-method", InvalidateMethodAsync).AcceptsQuery();
        group.MapPost("/clear", ClearAsync).AcceptsQuery();

        return group;
    }

    private static async Task<Ok<CacheStats>> GetStatsAsync(ICacheAdministrator admin, CancellationToken cancellationToken)
        => TypedResults.Ok(await admin.GetStatsAsync(cancellationToken).ConfigureAwait(false));

    private static Results<Ok<IReadOnlyList<CacheEntryInfo>>, ValidationProblem> ListEntries(
        ICacheAdministrator admin, string? storedKeyPrefix, string? tag, string? methodId, int? limit)
    {
        var take = limit ?? 100;
        if (take is < 1 or > MaxListLimit)
            return Invalid("limit", $"limit must be between 1 and {MaxListLimit}.");
        if (methodId is not null && string.IsNullOrWhiteSpace(methodId))
            return MissingMethodId();

        return TypedResults.Ok(admin.ListEntries(storedKeyPrefix, tag, take, methodId));
    }

    private static Results<Ok<CacheEntryInfo>, NotFound, ValidationProblem> GetEntry(ICacheAdministrator admin, string? storedKey)
    {
        if (string.IsNullOrEmpty(storedKey)) return MissingStoredKey();
        var entry = admin.GetEntry(storedKey);
        return entry is null ? TypedResults.NotFound() : TypedResults.Ok(entry);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> RemoveEntryAsync(
        ICacheAdministrator admin, string? storedKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(storedKey)) return MissingStoredKey();
        var removed = await admin.RemoveEntryAsync(storedKey, cancellationToken).ConfigureAwait(false);
        return removed ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, ValidationProblem>> InvalidateAsync(
        ICacheAdministrator admin, InvalidateRequest request, CancellationToken cancellationToken)
    {
        if (request.Tags is not { Count: > 0 } tags || tags.Any(string.IsNullOrWhiteSpace))
            return Invalid("tags", "Provide at least one non-empty tag.");

        await admin.InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem>> InvalidateMethodAsync(
        ICacheAdministrator admin, InvalidateMethodRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MethodId)) return MissingMethodId();

        try
        {
            await admin.InvalidateMethodAsync(request.MethodId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // An unknown id would otherwise remove nothing and still answer 204; the id is not echoed back
            return Invalid("methodId", "No cached method has this id: pass a methodId as methods lists it.");
        }

        return TypedResults.NoContent();
    }

    // The body is required, so a request without one (which a browser sends cross-site without a preflight) is a 400
    private static async Task<NoContent> ClearAsync(
        ICacheAdministrator admin, ClearRequest request, CancellationToken cancellationToken)
    {
        await admin.ClearAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static ValidationProblem MissingStoredKey()
        => Invalid("storedKey", "storedKey is required: pass the storedKey of an entry, as entries lists it.");

    private static ValidationProblem MissingMethodId()
        => Invalid("methodId", "methodId is required: pass the id of a cached method, as methods lists it.");

    private static ValidationProblem Invalid(string parameter, string message)
        => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [parameter] = [message] });

    /// <summary>Rejects, with 400, any query parameter other than <paramref name="parameters"/>.</summary>
    private static RouteHandlerBuilder AcceptsQuery(this RouteHandlerBuilder endpoint, params string[] parameters)
    {
        var accepted = parameters.Length == 0
            ? "Unknown query parameter. This endpoint takes none."
            : "Unknown query parameter. This endpoint takes " + string.Join(", ", parameters) + ".";
        return endpoint.AddEndpointFilter(async (context, next) =>
        {
            foreach (var name in context.HttpContext.Request.Query.Keys)
            {
                // Query binding ignores case, so the check does too
                if (!parameters.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return Invalid(name, accepted);
            }

            return await next(context).ConfigureAwait(false);
        });
    }
}

/// <summary>Body of the <c>invalidate</c> endpoint.</summary>
internal sealed record InvalidateRequest(IReadOnlyList<string>? Tags);

/// <summary>
/// Body of the <c>invalidate-method</c> endpoint. Any other property, such as <c>tags</c>, is rejected rather than ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InvalidateMethodRequest(string? MethodId);

/// <summary>
/// Body of the <c>clear</c> endpoint: <c>{}</c>. Clear takes no filter, so a body with any property, such as
/// <c>{ "tags": [...] }</c>, is rejected rather than read as a filtered removal that then empties the whole cache.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClearRequest;
