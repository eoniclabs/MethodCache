# MethodCache.AspNetCore

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

All paths are relative to the prefix (`/methodcache` by default).

| Method | Path | What it does |
|--------|------|--------------|
| `GET` | `stats` | Hits, misses, hit ratio, evictions, entries and estimated memory |
| `GET` | `methods` | The effective policy of every cached method and where it came from |
| `GET` | `entries?prefix=&tag=&limit=` | Live entries ordered by key (default limit 100, max 1000). Values are not returned |
| `GET` | `entry?key=` | One entry, or 404 |
| `DELETE` | `entry?key=` | Removes one entry, or 404 |
| `POST` | `invalidate` | Removes entries by tag. Body: `{ "tags": ["users"] }` |
| `DELETE` | `entries` | Removes every entry |

The same operations are available in code through `ICacheAdministrator` in the `MethodCache` namespace.

## Health check

`AddMethodCache()` on the health checks builder reports Healthy when the cache answers, with `entries`,
`hitRatio`, `evictions` and `memoryUsageBytes` in the report data. With Redis or SQL Server it reports Degraded
while that cache is unreachable and MethodCache serves from memory only (`distributedCacheAvailable` in the data).
Each check asks the distributed cache once, waiting at most its operation timeout.

## Limits

- Works with the default in-memory cache manager. A custom `ICacheManager` has no `ICacheAdministrator`.
- Policies are read-only here. Change them at runtime with `IRuntimeCacheConfigurator`.
