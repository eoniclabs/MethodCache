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

Disposing the cache manager disables its backplane callbacks and starts unsubscribing. Asynchronous service-provider
disposal waits for cleanup; a stalled custom backplane is bounded by `OperationTimeout` for the subscription wait
and again for the unsubscribe wait. If its subscription later finishes despite cancellation, MethodCache attempts
to unsubscribe again. Redis removes only its own handlers and preserves a connection supplied by the application.

## When the distributed cache is down

Calls keep working from memory and your methods; the distributed cache never fails or stalls a call. Each call to it
is capped at one second, a circuit breaker skips it until it is back, and invalidations are queued and retried
instead of throwing. Until they are stored, the instance doesn't read entries from it, so an invalidated value can't
come back. The [health check](monitoring.md#health-check) reports Degraded while this lasts.

A value set directly while the distributed cache is down stays in that instance's memory, and the value it replaced is
removed from the distributed cache and the other instances once it is back. An entry that comes back damaged (cut
short, or unreadable by the serializer) is a miss: your method runs and its result replaces the entry.

The timeout caps each call to the distributed cache, not a whole cache miss. A cache that stops answering costs a miss
one timeout, after which the circuit breaker skips it. One that answers just within the timeout costs a miss up to
three: reading the entry, writing your method's result and checking that no removal raced the write (and a fourth for
the backplane message).

## What other instances see

- An invalidation reaches another instance when the backplane delivers it. Until then, and for good if the message is
  lost or the instance is disconnected from the backplane, that instance serves its in-memory copy until it expires,
  as without a backplane. Instances without a copy read the distributed cache, which is already right.
- Messages carry no values, so a late, repeated or reordered message only makes an instance read the distributed cache
  again.
- An invalidation the distributed cache didn't accept is retried every `CircuitBreakerDuration`. Until the retry
  succeeds, other instances can serve the old value; the instance that invalidated never does.
- A write that timed out may still land later, because a distributed cache has no compare-and-set. Its key is removed
  again by the same retry, but until then the late value can replace a newer one, and a write that lands after the
  retry stays until it expires.
- Entries carry no checksum: damage that still reads as a valid value is served.

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
