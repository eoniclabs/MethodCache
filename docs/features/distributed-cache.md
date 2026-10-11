# Distributed cache

By default each application instance has its own in-memory cache. Add a distributed cache to share results between
instances and keep them across restarts:

<!-- snippet: distributed-redis -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");   // MethodCache.Redis
```
<!-- endSnippet -->

<!-- snippet: distributed-sqlserver -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheSqlServer(connectionString);   // MethodCache.SqlServer
```
<!-- endSnippet -->

## How it works

- Results live in memory (L1) and in the distributed cache (L2). An in-memory miss reads the distributed cache before
  running your method, and a new result is written to both.
- Invalidating a key or tag removes it from the distributed cache, and a backplane tells every other instance to drop
  its in-memory copy at once: Redis pub/sub, or a table that SQL Server instances poll.
- `[Cache]` methods and `GetOrCreateAsync` need no changes.
- MethodCache keeps its own distributed cache and connection, so your application's `IDistributedCache` is left alone.

## When the distributed cache is down

Calls keep working from memory and your methods; the distributed cache never fails or stalls a call. Each call to it
is capped at one second, a circuit breaker skips it until it is back, and invalidations are queued and retried
instead of throwing. Until they are stored, the instance doesn't read entries from it, so an invalidated value can't
come back. The [health check](monitoring.md#health-check) reports Degraded while this lasts.

## Serialization

Values are written as JSON with System.Text.Json. The `MethodCache.Serialization.MessagePack` package makes them
smaller and faster to read:

<!-- snippet: distributed-messagepack -->
```cs
builder.Services.AddMethodCacheRedis("localhost:6379");
builder.Services.AddMethodCacheMessagePack();   // MethodCache.Serialization.MessagePack
```
<!-- endSnippet -->

Each entry records its format, so after a switch older entries count as misses. Under Native AOT, register your
`JsonSerializerContext` ([Native AOT](native-aot.md)).

## Any IDistributedCache

<!-- snippet: distributed-custom -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheDistributedCache(
    _ => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));   // any IDistributedCache
```
<!-- endSnippet -->

Without a backplane, other instances keep their in-memory copy of an invalidated entry until it expires. Register an
`IBackplane` (in `MethodCache.Extensibility`) to send invalidations to them.

## Limits

- Pattern invalidation (`InvalidateByTagPatternAsync`) throws `NotSupportedException` with a distributed cache.
- [Fail-safe](fail-safe.md) values and [runtime overrides](configuration.md#runtime-overrides) live in each instance's
  memory.

## Package guides

The [Redis](../../src/MethodCache.Redis/README.md) and [SQL Server](../../src/MethodCache.SqlServer/README.md) READMEs
cover their options, shared connections, table creation and outage settings.
