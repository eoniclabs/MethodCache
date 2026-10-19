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
| `GET` | `methods` | The effective policy of every cached method and where it came from |
| `GET` | `entries?prefix=&tag=&limit=` | Live entries in the memory of the instance that answers, ordered by key (default limit 100, max 1000). Values are not returned |
| `GET` | `entry?key=` | One entry, by the `storedKey` that `entries` lists, in the memory of the instance that answers, or 404 |
| `DELETE` | `entry?key=` | Removes one entry, also from Redis or SQL Server and the other instances when configured. 404 only without a distributed cache, when the key isn't in memory |
| `POST` | `invalidate` | Removes entries by tag. Body: `{ "tags": ["users"] }` |
| `DELETE` | `entries` | Removes every entry |

The same operations are available in code through `ICacheAdministrator` in the `MethodCache` namespace (see
[Monitoring](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/monitoring.md)).

## Health check

`AddMethodCache()` on the health checks builder reports `managerCapability` (`built_in`, `noop`, `custom`) and
`statisticsAvailable`. The built-in manager adds `localMethodEntries`, `localHttpEntries`, `localHttpBytes`,
`observedDistributedEntries`, `totalWeight` and `evictions`. Weight uses the configured weigher's units, or one per
method entry. There is no estimated method heap size or fabricated custom-manager storage count.

With an owned distributed transport the report includes `distributedCacheAvailable`, `backplaneStatus`
(`not_configured`, `connecting`, `connected`, `disconnected`, `unknown`), `invalidationQueued` and `publicationQueued`.
L2 failure, either recovery queue, or an unknown/not established configured backplane uses the registration's
`failureStatus` (default `Degraded`). A healthy L2 probe alone does not prove backplane subscription. The probe respects
the distributed operation timeout. Error reports include exception types, never backend exception messages.

## Limits

- Works with the default in-memory cache manager. A custom `ICacheManager` has no `ICacheAdministrator`.
- Policies are read-only here. Change them at runtime with `IRuntimeCacheConfigurator`.
