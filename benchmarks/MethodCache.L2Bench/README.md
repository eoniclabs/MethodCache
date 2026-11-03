# MethodCache.L2Bench

Times Redis, SQL Server and PostgreSQL as MethodCache's distributed cache, side by side. The results are in
[docs/benchmarks.md](../../docs/benchmarks.md#distributed-cache-providers-2026-11-03).

It needs a running server for every provider it measures, so CI only builds it. Connection strings come from
environment variables and are never stored here:

| Variable | Meaning | Default |
|---|---|---|
| `REDIS` | StackExchange.Redis configuration | `localhost:6379` |
| `SQL` | SQL Server connection string | required when SQL Server runs |
| `PG` | PostgreSQL connection string | required when PostgreSQL runs |
| `BACKENDS` | Providers to measure, comma separated | `Redis,SqlServer,PostgreSql` |
| `POLL_MS` | Backplane polling interval for SQL Server and PostgreSQL | `1000` |
| `SECONDS_PER_RUN` | Length of each throughput run | `10` |

```bash
export PG='Host=localhost;Username=postgres;Password=...;Database=methodcache_bench'
BACKENDS=PostgreSql dotnet run -c Release --project benchmarks/MethodCache.L2Bench -- raw
```

Modes:

- `raw`: each provider's own `IDistributedCache`, one call at a time: read a value, read a missing key, write a new
  value and replace a live one, at 256 B, 4 KB and 64 KB.
- `throughput`: 1 KB values over 10,000 keys, 90 % reads, with 1, 16 and 64 concurrent callers.
- `backplane`: a message published on one instance until another instance receives it (500 messages, 5 ms apart).
- `e2e`: a generated `[Cache]` method through MethodCache: in-memory hit, hit in the distributed cache, full miss, and a
  tag invalidation until another instance runs the method again.
- `miss`: the full miss path alone.
- `all` (the default): `raw`, `throughput`, `backplane` and `e2e`.

Run it on a quiet machine with each server next to the app, and compare the columns rather than the absolute values.
`backplane` reads MethodCache's internal backplane directly, which is why Core lets this assembly see its internals.
