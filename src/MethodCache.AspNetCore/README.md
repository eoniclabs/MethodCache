# MethodCache.AspNetCore

Requires .NET 10 and the .NET 10 SDK; use the same package version as `MethodCache`.

Install this package and `MethodCache` at the exact same version. Integration packages use Core internals;
do not override their exact Core dependency. Our package smoke tests promote NU1608 to a restore error.

Admin endpoints and a health check for MethodCache's in-memory cache.

## Setup

<!-- snippet: aspnetcore-setup -->
```cs
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
builder.Services.AddHealthChecks().AddMethodCache();
builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireRole("Admin")));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapMethodCacheAdmin("CacheAdmins");
app.MapHealthChecks("/health");
```
<!-- endSnippet -->

The authorization policy is required, because the endpoints expose cache keys and can empty the cache; a
signed-in user without the policy gets 403.

## Endpoints

All paths are relative to the prefix, `/methodcache` by default. `MapMethodCacheAdmin("CacheAdmins", "/ops/cache")`
maps them under `/ops/cache` instead.

| Method | Path | What it does |
|--------|------|--------------|
| `GET` | `stats` | Local hits/misses/evictions, method/HTTP entries, HTTP bytes, observed L2 entries and configured method weight |
| `GET` | `methods` | Every cached method: its `methodId`, effective policy and where each setting comes from |
| `GET` | `entries?storedKeyPrefix=&tag=&methodId=&limit=` | Live entries in the memory of the instance that answers, each with its `storedKey` and `methodId`, ordered by `storedKey` (default limit 100, max 1000). `methodId` must be an id that `methods` lists. Values are not returned |
| `GET` | `entry?storedKey=` | One entry, by the `storedKey` that `entries` lists, in the memory of the instance that answers, or 404 |
| `DELETE` | `entry?storedKey=` | Removes one entry, also from Redis or SQL Server and the other instances when configured. 404 only without a distributed cache, when the key isn't in memory |
| `POST` | `invalidate` | Removes entries by tag. Body: `{ "tags": ["users"] }` |
| `POST` | `invalidate-method` | Removes every entry of one cached method, also from the distributed cache and the other instances. Body: `{ "methodId": "[MyApp]MyApp.IUserService.GetUserAsync" }`, an id that `methods` lists |
| `POST` | `clear` | Removes every entry. Body: `{}` |

Answers other than 200, 204 and 404:

- **400**, and nothing changes: a query parameter the endpoint doesn't take, a missing or empty `storedKey` or
  `methodId`, a `methodId` that no cached method has, a `limit` out of range, an empty tag list, or a body with a
  property the endpoint doesn't take (`invalidate` takes only `tags`, `invalidate-method` only `methodId`, `clear`
  none). A `POST` without a body is 400 too.
- **415**: a `POST` whose body isn't `application/json`.
- **503**: the removal is done in this instance's memory but the distributed cache hasn't confirmed it yet. The
  cache retries on its own; don't repeat the call.
- **401** and **403**: the caller isn't signed in, or doesn't satisfy the policy.

The same operations are available in code through `ICacheAdministrator` in the `MethodCache` namespace (see
[Monitoring](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/monitoring.md)).

## Health check

`AddMethodCache()` on the health checks builder reports:

| Key | Value |
|---|---|
| `managerCapability` | `built_in`, `noop` or `custom` (your own `ICacheManager`) |
| `statisticsAvailable` | Whether the statistics below are reported |
| `methodStatisticsAvailable` | Whether method statistics are reported: only for the built-in manager |
| `httpCapability` | `not_configured`, `local` or `distributed`: whether `AddHttpCaching` is registered, and where it stores responses |
| `localMethodEntries`, `totalWeight`, `evictions` | Built-in manager only. Weight uses the configured weigher's units, or one per method entry |
| `localHttpEntries`, `localHttpBytes`, `observedDistributedEntries` | When statistics are available |
| `queued`, `publicationQueued` | Invalidations waiting for the distributed cache, and for the backplane |
| `backplaneStatus` | `not_configured`, `connecting`, `connected`, `disconnected` or `unknown` |
| `distributedCacheAvailable` | Whether the distributed cache answers; only when one is configured |
| `error.type` | Only when the check itself failed: the exception type |

There is no estimated method heap size or fabricated custom-manager storage count.

L2 failure, either recovery queue, or an unknown/not established configured backplane uses the registration's
`failureStatus` (default `Degraded`). A healthy L2 probe alone does not prove backplane subscription. The probe respects
the distributed operation timeout. Error reports include exception types, never backend exception messages.

## Limits

- Works with the default in-memory cache manager. With your own `ICacheManager`, the endpoints need the
  `ICacheAdministrator` that `AddHttpCaching` registers, and it covers HTTP responses only: `methods` is empty,
  `clear` removes HTTP responses only, and `invalidate-method` answers 400.
- Policies are read-only here. Change them at runtime with `IRuntimeCacheConfigurator`.
