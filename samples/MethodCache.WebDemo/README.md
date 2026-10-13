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

## Admin endpoints

The `/methodcache` endpoints show keys and can empty the cache, so they require an authenticated user. This demo
opens them in the Development environment only; see `Program.cs`.

The web demo uses a shared SQLite database in the temp folder (`methodcache-webdemo-products.db`), so concurrent price updates from either demo instance are atomic. Decimal prices retain full precision. The earlier temporary JSON file is left untouched; the SQLite catalog starts with the demo seed data.
