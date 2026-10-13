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
- Invalidating a key removes it from the distributed cache. Invalidating a tag records when it happened, so older
  entries with that tag read as misses. A backplane tells every other instance to drop its in-memory copy: at once
  with Redis pub/sub, or on their next poll (every second by default) with SQL Server.
- Only Redis and Microsoft SQL Server have packages. Another store, such as PostgreSQL, needs a
  [custom provider](#custom-distributed-caches).
- `[Cache]` methods and `GetOrCreateAsync` need no changes.
- MethodCache keeps its own distributed cache and connection, so your application's `IDistributedCache` is left alone.

Run the same MethodCache version on every instance that shares a cache and backplane; messages from another version
are ignored with one warning.

## When the distributed cache is down

Calls keep working from memory and your methods; the distributed cache never fails or stalls a call. Each call to it
is capped at `OperationTimeout` (one second), a circuit breaker skips it until it is back, and invalidations are queued and retried
instead of throwing. Until they are stored, the instance doesn't read entries from it, so an invalidated value can't
come back. The [health check](monitoring.md#health-check) reports Degraded while this lasts.

A value set directly while the distributed cache is down stays in that instance's memory, and the value it replaced is
removed from the distributed cache and the other instances once it is back. An entry that comes back damaged (cut
short, or unreadable by the serializer) is a miss: your method runs and its result replaces the entry.

The timeout caps each call to the distributed cache, not a whole cache miss. A cache that stops answering costs a miss
one timeout, after which the circuit breaker skips it. One that answers just within the timeout costs a miss up to
three: reading the entry, writing your method's result and checking that no removal raced the write (and a fourth for
the backplane message).

## Options

`DistributedCacheOptions` apply to Redis, SQL Server and custom distributed caches alike:

<!-- snippet: distributed-outage-options -->
```cs
builder.Services.Configure<DistributedCacheOptions>(options =>
{
    options.OperationTimeout = TimeSpan.FromMilliseconds(500);
    options.CircuitBreakerDuration = TimeSpan.FromSeconds(30);
});
```
<!-- endSnippet -->

| Option | Default | |
|---|---|---|
| `OperationTimeout` | 1 second | The longest one call to the distributed cache may take. A slower call is abandoned and counts as a failure |
| `CircuitBreakerDuration` | 10 seconds | How long the distributed cache is skipped after a failure or timeout. Invalidations it didn't accept are retried at this interval |
| `ClockSkewTolerance` | 2 seconds | How far apart your servers' clocks may be. A tag invalidation also applies to entries created up to this long after it |
| `TagCheckInterval` | 1 second | How long an instance trusts the invalidation time it last read for a tag. Without a backplane, an entry read from the distributed cache can outlive a tag invalidation made on another instance by up to this long |
| `TagRetention` | 1 day | How long tag invalidations are kept. An entry stays in the distributed cache at most `TagRetention - ClockSkewTolerance` from when your method started loading it (less if its own duration is shorter); a load that takes longer is kept only in that instance's memory. Both values are truncated to whole milliseconds, and `TagRetention` must exceed `ClockSkewTolerance` by at least one millisecond, or startup fails |
| `KeyPrefix` | `methodcache:` | Prefix of every key MethodCache writes. `AddMethodCacheRedis` and `AddMethodCacheSqlServer` set it from their own `KeyPrefix`; give each application its own |

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
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");
builder.Services.AddMethodCacheMessagePack();   // MethodCache.Serialization.MessagePack
```
<!-- endSnippet -->

Entries written in the other format read as misses and are replaced, so switching needs no flush. See the
[MessagePack package guide](../../src/MethodCache.Serialization.MessagePack/README.md). Under Native AOT, register your
`JsonSerializerContext` ([Native AOT](native-aot.md)).

## Custom distributed caches

<!-- snippet: distributed-custom -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheDistributedCache(
    _ => customCache);   // IDistributedCache with backend-atomic invalidation stamps
```
<!-- endSnippet -->

`customCache` implements `IAtomicInvalidationStore` (in `MethodCache.Extensibility`), which extends
`IDistributedCache`. MethodCache rejects a provider without this capability, such as Microsoft's
`MemoryDistributedCache` (`AddDistributedMemoryCache()`), `RedisCache` or `SqlServerCache`: with a generic host,
`host.StartAsync()` throws an `OptionsValidationException` naming `IAtomicInvalidationStore`, so the app fails at
startup rather than on its first request; without a host, resolving the cache manager throws an `ArgumentException`.
Implement `WriteInvalidationStampAsync` with a backend atomic maximum: keep the highest unexpired
signed timestamp, expose it as eight little-endian bytes through `GetAsync`, and never shorten an unexpired
stamp’s retention. Expired stamps can be replaced. A process-local lock or a read followed by `SetAsync` is
insufficient for a backend shared by different processes. Redis uses one Lua operation; SQL Server uses a
transaction with a key/range update lock. This applies to tag, clear and key-removal stamps.

The [HTTP response cache](http-caching.md) has its own contract and works with a plain `IDistributedCache`.

Without a backplane, other instances keep their in-memory copy of an invalidated entry until it expires. Register an
`IBackplane` (in `MethodCache.Extensibility`) to send invalidations to them. Disposing the service provider
unsubscribes from the backplane; a backplane that stalls delays disposal by at most twice `OperationTimeout`.

## Limits

- Pattern invalidation (`InvalidateByTagPatternAsync`) throws `NotSupportedException` with a distributed cache.
- [Fail-safe](fail-safe.md) values and [runtime overrides](configuration.md#runtime-overrides) live in each instance's
  memory.

## Package guides

The [Redis](../../src/MethodCache.Redis/README.md) and [SQL Server](../../src/MethodCache.SqlServer/README.md) READMEs
cover their options, shared connections, table creation, outage settings and how each stores keys and invalidations.
The SQL Server guide also compares the two and has the schema migration for an existing invalidations table.

