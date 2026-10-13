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

Queued invalidations are stored a batch at a time once the cache answers, so a long outage's backlog drains at the
pace the cache can take. More than 10,000 are replaced by one clear of the distributed cache (logged as a warning),
and so is an invalidation the cache keeps refusing while it answers everything else, so neither holds up the rest.
The queue lives in memory: when the instance shuts down, it tries once more to store what is queued, for about one
`OperationTimeout`, and logs a warning with the number it had to drop. Other instances can read those invalidated
values from the distributed cache until they expire.

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
| `OperationTimeout` | 1 second | The longest one call to the distributed cache may take. A slower call is abandoned and counts as a failure, except a write (see [What other instances see](#what-other-instances-see)) |
| `CircuitBreakerDuration` | 10 seconds | How long the distributed cache is skipped after a failure or timeout. Invalidations it didn't accept are retried at this interval |
| `ClockSkewTolerance` | 2 seconds | How far apart your servers' clocks may be. A tag invalidation also applies to entries created up to this long after it. One stamped further ahead of an instance's clock (by a server whose clock runs fast) counts from when that instance first read it, until its clock reaches the stamp |
| `TagCheckInterval` | 1 second | How long an instance trusts the invalidation time it last read for a tag. Without a backplane, an entry read from the distributed cache can outlive a tag invalidation made on another instance by up to this long |
| `TagRetention` | 1 day | How long tag invalidations are kept. An entry stays in the distributed cache at most `TagRetention - ClockSkewTolerance` from when your method started loading it (less if its own duration is shorter); a load that takes longer is kept only in that instance's memory. Both values are truncated to whole milliseconds, and `TagRetention` must exceed `ClockSkewTolerance` by at least one millisecond, or startup fails; with SQL Server it must also end before 9999-12-31 |
| `KeyPrefix` | `methodcache:` | Prefix of every key MethodCache writes. `AddMethodCacheRedis` and `AddMethodCacheSqlServer` set it from their own `KeyPrefix`; give each application its own |

## What other instances see

- An invalidation reaches another instance when the backplane delivers it. Until then, and for good if the message is
  lost or the instance is disconnected from the backplane, that instance serves its in-memory copy until it expires,
  as without a backplane. Instances without a copy read the distributed cache, which is already right.
- Messages carry no values, so a late, repeated or reordered message only makes an instance read the distributed cache
  again.
- An invalidation the distributed cache didn't accept is retried every `CircuitBreakerDuration`. Until the retry
  succeeds, other instances can serve the old value; the instance that invalidated never does.
- A write that timed out (a large value, say) doesn't open the circuit breaker, since the cache may answer everything
  else: the instance logs a warning and keeps using it. Such a write may still land later, because a distributed cache
  has no compare-and-set, so its key is removed again after `CircuitBreakerDuration` (as after a write that failed,
  once the cache answers). Further timed-out writes of the key push that one removal back, and an instance that shuts
  down first removes the key while it shuts down. Until then the late value can replace a newer one, and a write that
  lands after that removal stays until it expires.
- Entries carry no checksum: damage that still reads as a valid value is served.

## Serialization

Values are written as JSON with System.Text.Json, public fields included, so value tuples and structs with fields
are shared as they are. For each method and each type it returns, the first value an instance writes is read back and
written again; when that doesn't give the same bytes and type, the method's values of that type stay in each
instance's memory only, and one warning names the method and the type. That happens with properties that have a
private or protected setter, public readonly fields of a type that also has a parameterless constructor, a derived
type returned as its declared base type (an `Animal` method returning a `Dog`, even after it returned plain
`Animal`s), and `object`, with JSON and MessagePack alike. A value the serializer throws on (a cycle, a delegate
field) stays in memory the same way. A collection declared as an interface (`IReadOnlyList<int>`,
`IEnumerable<int>`) may come back as another implementation, a `List<int>` for an array, and is still shared. The
check looks at the type of the value itself, not of what it holds: a `Dog` inside a `List<Animal>` or in a property
declared `Animal` is shared without its `Dog` data. To share such values, register a serializer that reads them back,
such as `JsonCacheSerializer` with your own `JsonSerializerOptions` or `JsonSerializerContext` (`[JsonInclude]` on the
setter, `[JsonDerivedType]` on the base type), or your own `ISerializer`. The `MethodCache.Serialization.MessagePack` package makes values
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

## Keep invalidations from being evicted

Tag, clear and key-removal stamps are ordinary expiring keys next to the entries they invalidate. A store that evicts a
stamp under memory pressure while an entry it invalidated stays makes that entry valid again, on every instance, until
it expires. Give the store room, and don't let it evict MethodCache's keys: for Redis, use `maxmemory-policy
noeviction` (or a Redis of its own). Every MethodCache key has a time to live, so the `volatile-*` policies can evict
them as well as the `allkeys-*` ones. With `noeviction`, a full Redis refuses writes instead: MethodCache then treats it
as down and answers from memory. SQL Server removes rows only once they expired.

## Limits

- Pattern invalidation (`InvalidateByTagPatternAsync`) throws `NotSupportedException` with a distributed cache.
- [Fail-safe](fail-safe.md) values and [runtime overrides](configuration.md#runtime-overrides) live in each instance's
  memory.

## Package guides

The [Redis](../../src/MethodCache.Redis/README.md) and [SQL Server](../../src/MethodCache.SqlServer/README.md) READMEs
cover their options, shared connections, table creation, outage settings and how each stores keys and invalidations.
The SQL Server guide also compares the two and has the schema migration for an existing invalidations table.

An explicit `SetAsync` with a lifetime shorter than L2 millisecond precision removes the previous distributed value instead of leaving it readable. If the backend is unavailable, the removal follows the normal invalidation retry path.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. Shared work may finish for other callers; cancellation is not reported as a dependency failure.
