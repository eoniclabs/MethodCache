# MethodCache.AspNetCore

Admin endpoints and a health check for MethodCache's in-memory cache.

## Setup

```csharp
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
builder.Services.AddHealthChecks().AddMethodCache();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapMethodCacheAdmin().RequireAuthorization("CacheAdmins");
app.MapHealthChecks("/health");
```

The endpoints require an authenticated user by default, because they expose cache keys and can empty the
cache. Add `.RequireAuthorization("policy")` to require a specific policy.

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

The same operations are available in code through `ICacheAdministrator` from `MethodCache.Core`.

## Health check

`AddMethodCache()` on the health checks builder reports Healthy when the cache answers, with `entries`,
`hitRatio`, `evictions` and `memoryUsageBytes` in the report data.

## Limits

- Works with the default in-memory cache manager. A custom `ICacheManager` has no `ICacheAdministrator`.
- Policies are read-only here. Changing a method's policy at runtime will be added when generated decorators
  read their policy per call.
