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

Chapters 7–10 live in [AdvancedExamples.cs](AdvancedExamples.cs) and use the generated decorators for the
interfaces in [AdvancedServices.cs](AdvancedServices.cs). A registered `FakeTimeProvider` advances expiration
and retry windows immediately. A completion gate holds a refresh in progress so the example can prove that
the caller receives the old value before the background load finishes. The docs tests run these same scenarios.

The tenant IDs are illustrative. In an application, derive the tenant from an authorized identity before calling
the service; including it in a cache key does not authorize access.

The [HttpClient tour](../MethodCache.HttpDemo/) demonstrates HTTP response caching against a local server.
The [web demo](../MethodCache.WebDemo/) adds real endpoints, SQLite, Redis and OpenTelemetry.
