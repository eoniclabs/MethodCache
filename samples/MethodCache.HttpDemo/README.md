# HttpClient caching demo

Run from the repository root with the .NET 10 SDK:

```bash
dotnet run -c Release --project samples/MethodCache.HttpDemo
```

The demo starts a real HTTP server on an available loopback port, registers a typed `ProductApiClient` with
`AddHttpCaching()`, and stops the server when finished. It needs no Docker, credentials or external API.
Each claim is checked; a failed claim produces a nonzero exit code.

| Scenario | Origin calls and result |
|---|---|
| `Cache-Control: max-age=60` | Two GETs make one origin call; the second response has `X-Cache: HIT` |
| ETag after expiry | Advance the clock 61 seconds; the origin returns 304, the caller receives the complete cached 200 response |
| `no-store` | Two GETs both reach the origin and return different responses |
| PUT to the cached URL | The next GET reaches the origin and returns the updated price |
| Read after the update | The new response is cached without another origin call |

[ProductApiClient.cs](ProductApiClient.cs) is the ordinary typed client. [HttpExamples.cs](HttpExamples.cs)
contains the local server, registration, source-call counters and checks. Both the console entry point and
`AdvancedDemoTests` run that same scenario. HTTP freshness reads a registered `FakeTimeProvider`, and the local
server sets its Date header from that clock; no expiry sleeps are needed.

`X-Cache` is enabled for the example. See the [HTTP caching guide](../../docs/features/http-caching.md) and
[package README](../../src/MethodCache.HttpCaching/README.md) for private/shared caches, Vary, distributed
storage and stale-response options.
