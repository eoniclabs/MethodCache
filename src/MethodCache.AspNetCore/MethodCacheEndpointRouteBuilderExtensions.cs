using MethodCache;
using MethodCache.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

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
    /// <item><c>GET entries?prefix=&amp;tag=&amp;limit=</c>: live entries, without their values</item>
    /// <item><c>GET entry?key=</c> and <c>DELETE entry?key=</c>: one entry, by the <c>storedKey</c> that <c>entries</c> lists</item>
    /// <item><c>POST invalidate</c> with <c>{ "tags": [...] }</c>: remove entries by tag</item>
    /// <item><c>DELETE entries</c>: remove every entry</item>
    /// </list>
    /// The endpoints expose cache keys and can empty the cache, so every request must satisfy the authorization
    /// policy <paramref name="policyName"/>. A signed-in user is not enough on its own.
    /// </summary>
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

        group.MapGet("/stats", GetStatsAsync);
        group.MapGet("/methods", (ICacheAdministrator admin) => TypedResults.Ok(admin.GetMethods()));
        group.MapGet("/entries", ListEntries);
        group.MapGet("/entry", GetEntry);
        group.MapDelete("/entry", RemoveEntryAsync);
        group.MapPost("/invalidate", InvalidateAsync);
        group.MapDelete("/entries", async (ICacheAdministrator admin, CancellationToken cancellationToken) =>
        {
            await admin.ClearAsync(cancellationToken).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        return group;
    }

    private static async Task<Ok<CacheStats>> GetStatsAsync(ICacheAdministrator admin, CancellationToken cancellationToken)
        => TypedResults.Ok(await admin.GetStatsAsync(cancellationToken).ConfigureAwait(false));

    private static Results<Ok<IReadOnlyList<CacheEntryInfo>>, ValidationProblem> ListEntries(
        ICacheAdministrator admin, string? prefix, string? tag, int? limit)
    {
        var take = limit ?? 100;
        if (take is < 1 or > MaxListLimit)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = new[] { $"limit must be between 1 and {MaxListLimit}." }
            });
        }

        return TypedResults.Ok(admin.ListEntries(prefix, tag, take));
    }

    private static Results<Ok<CacheEntryInfo>, NotFound> GetEntry(ICacheAdministrator admin, string key)
    {
        var entry = admin.GetEntry(key);
        return entry is null ? TypedResults.NotFound() : TypedResults.Ok(entry);
    }

    private static async Task<Results<NoContent, NotFound>> RemoveEntryAsync(
        ICacheAdministrator admin, string key, CancellationToken cancellationToken)
    {
        var removed = await admin.RemoveEntryAsync(key, cancellationToken).ConfigureAwait(false);
        return removed ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, ValidationProblem>> InvalidateAsync(
        ICacheAdministrator admin, InvalidateRequest request, CancellationToken cancellationToken)
    {
        if (request.Tags is not { Count: > 0 } tags || tags.Any(string.IsNullOrWhiteSpace))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["tags"] = new[] { "Provide at least one non-empty tag." }
            });
        }

        await admin.InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}

/// <summary>Body of the <c>invalidate</c> endpoint.</summary>
internal sealed record InvalidateRequest(IReadOnlyList<string>? Tags);
