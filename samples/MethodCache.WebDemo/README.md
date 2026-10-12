# MethodCache web demo

An ASP.NET Core API whose product catalog is cached with `[Cache]`, plus the admin endpoints, a health check,
OpenTelemetry and, optionally, Redis shared by two instances.

## Run it

```bash
dotnet run --project samples/MethodCache.WebDemo
```

Then open [MethodCache.WebDemo.http](MethodCache.WebDemo.http) in Rider or Visual Studio and send the requests
in order, or use curl:

```bash
curl -i localhost:5080/products/1     # X-Elapsed-Ms: ~300 (the "database")
curl -i localhost:5080/products/1     # X-Elapsed-Ms: ~1   (the cache)
curl -X PUT localhost:5080/products/1/price -H 'Content-Type: application/json' -d '{"price":119}'
curl -i localhost:5080/products/1     # loaded again, new price
curl localhost:5080/methodcache/stats
```

Everything MethodCache-specific is in [Program.cs](Program.cs) and [Catalog.cs](Catalog.cs).
The simulated 300 ms database latency makes hits easy to see. `GET /demo/queries` reports actual database
read attempts for this instance, so the proof does not depend on response timings.

## Repeatable data

[ProductStore.cs](ProductStore.cs) uses SQLite with transactions, so two processes share data without losing
each other's price updates. By default the database is `methodcache-webdemo/products.sqlite` in the system
temp directory. Both Redis profiles use that same file. Set `Demo__DataFile` to an absolute path to choose
another database, `Demo__QueryDelayMs` to change the simulated latency, and `Demo__CachePrefix` to isolate
another run's Redis cache. Instances sharing data must also use the same cache prefix.

`POST /demo/reset` restores all four seed products, resets this instance's query counter, and invalidates
product/list caches everywhere. Reset and query-counter endpoints exist only in Development. A missing, null or
negative price returns 400 without changing stored or cached products; zero is a valid price. Updating a missing
product returns 404.

## Two instances sharing Redis

```bash
docker compose -f samples/MethodCache.WebDemo/docker-compose.yml up -d    # Redis and the Aspire dashboard
dotnet run --project samples/MethodCache.WebDemo --launch-profile redis-a  # http://localhost:5081
dotnet run --project samples/MethodCache.WebDemo --launch-profile redis-b  # http://localhost:5082
```

1. `GET :5081/products/3` runs the query and stores the result in memory and in Redis.
2. `GET :5082/products/3` is answered from Redis, without querying the database.
3. `PUT :5081/products/3/price` invalidates the product everywhere: instance B drops its in-memory copy too.
4. Open the dashboard at http://localhost:18888 to see a trace for every request, with MethodCache's hits and
   misses inside it, and the cache metrics.

[MethodCache.Redis.http](MethodCache.Redis.http) walks through both instances, including a cached product
that survives another product's price change and per-instance database counters.

The default clock-skew tolerance is two seconds: entries loaded within that window after a tag invalidation
can be rejected by another instance. For a fresh shared-cache demonstration, reset and wait at least three
seconds **before** the first product GET. This is a conservative freshness check, not a loss of updated data.

## Redis outage and recovery

With the Redis profiles running, load product 1 on both instances and product 2 on A. Stop the demo Redis:

```bash
docker compose -f samples/MethodCache.WebDemo/docker-compose.yml stop redis
```

Product 2 on A remains a memory hit. A cold product can still be loaded from SQLite; after a Redis failure,
`GET /health` reports `Degraded`. Change product 1's price on A: A sees the new value, while disconnected B
can retain its old in-memory value. The invalidation is queued. Start Redis again:

```bash
docker compose -f samples/MethodCache.WebDemo/docker-compose.yml start redis
```

After reconnection and the retry window (default ten seconds), the queued invalidation reaches Redis and B.
Health recovers and B reads the new price. Memory hits require no Redis round trip; cold requests can pay the
configured distributed-operation timeout while a failure is being detected.

## Automated verification

```bash
bash build/ci/jobs.sh demo       # eleven-chapter console tour (including HttpClient) and web checks without Docker
bash build/ci/jobs.sh webdemo    # all web checks, including a private Redis container (requires Docker)
```

Run `bash build/ci/jobs.sh build` first. `WebDemoCacheTests` checks hits, 20 simultaneous requests, selective
invalidation, reset, concurrent writes, persistence and the HTTP request file. `WebDemoRedisTests` starts two
actual web hosts sharing SQLite/Redis, then stops and restarts only its own Redis container to verify fallback,
degraded health and queued invalidation. Those tests use a registered fake clock for cache/retry windows and
transport events for reconnection. They are included in the regular Docker-required CI test job; `unit` excludes
them with the `RedisDemo` category. Every test uses a temporary database and a unique Redis prefix.

## Admin endpoints

The `/methodcache` endpoints show keys and can empty the cache, so they require an authenticated user. This demo
opens them in the Development environment only; see `Program.cs`.

Outside Development, anonymous admin requests return 401 and signed-in users without the `Admin` role get 403.
The demo registers cookie authentication to handle those responses, but has no sign-in page. To use the admin
API in a deployed application, configure your application's authentication and grant operators the `Admin` role.
