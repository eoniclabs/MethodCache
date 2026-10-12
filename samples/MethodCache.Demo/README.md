# MethodCache console tour

Run from the repository root with the .NET 10 SDK:

```bash
dotnet run -c Release --project samples/MethodCache.Demo
```

Every chapter checks its own claims; a failed check makes the process return a nonzero exit code.

| Chapter | What it proves |
|---|---|
| 1. One attribute | A million cached calls execute the weather service once |
| 2. Stampede protection | 100 concurrent calls share one load |
| 3. Argument tags | Renaming one user invalidates only that user's profile |
| 4. Configuration | Defaults, attributes, live JSON reload and runtime overrides |
| 5. Third-party code | Cache a vendor interface without changing its implementation |
| 6. Monitoring | Hits, misses, entries and tag invalidation |
| 7. Expiration | Absolute TTL, sliding expiration and refresh-ahead |
| 8. Fail-safe | Last good value on failure/timeout, retry suppression and recovery |
| 9. Conditional caching | Missing products are not stored; newly created ones appear immediately |
| 10. Tenants | The same product ID has separate cache keys and invalidation tags for each tenant |
| 11. HttpClient | HTTP cache hits, ETag/304 validation, no-store and invalidation after PUT |

Chapters 7–10 live in [AdvancedExamples.cs](AdvancedExamples.cs) and use the generated decorators for the
interfaces in [AdvancedServices.cs](AdvancedServices.cs). A registered `FakeTimeProvider` advances expiration
and retry windows immediately. A completion gate holds a refresh in progress so the example can prove that
the caller receives the old value before the background load finishes. The docs tests run these same scenarios.

The tenant IDs are illustrative. In an application, derive the tenant from an authorized identity before calling
the service; including it in a cache key does not authorize access.

## HttpClient caching

Chapter 11 starts a real HTTP server on an available loopback port and stops it after checking each claim.
It needs no Docker, credentials or external API and runs as part of the same console command above.

| Scenario | Origin calls and result |
|---|---|
| `Cache-Control: max-age=60` | Two GETs make one origin call; the second response has `X-Cache: HIT` |
| ETag after expiry | Advance the clock 61 seconds; the origin returns 304, the caller receives the complete cached 200 response |
| `no-store` | Two GETs both reach the origin and return different responses |
| PUT to the cached URL | The next GET reaches the origin and returns the updated price |
| Read after the update | The new response is cached without another origin call |

[ProductApiClient.cs](ProductApiClient.cs) is the ordinary typed client. [HttpExamples.cs](HttpExamples.cs)
contains its local server, registration and checks; the console and `AdvancedDemoTests` run that same scenario.
The transport explicitly uses `UseCookies=false`, so automatic transport cookies cannot hide caller identity
from the HTTP cache. Freshness uses the registered demo clock and the origin's matching Date header.
`Microsoft.Extensions.TimeProvider.Testing` is only used to advance this demo's clock; production applications
can register `TimeProvider.System` instead. The ASP.NET Core framework reference provides the local origin server.

`X-Cache` is enabled for the example. See the [HTTP caching guide](../../docs/features/http-caching.md) and
[package README](../../src/MethodCache.HttpCaching/README.md) for private/shared caches, Vary, distributed
storage and stale-response options.

The [web demo](../MethodCache.WebDemo/) adds real endpoints, SQLite, Redis and OpenTelemetry.
