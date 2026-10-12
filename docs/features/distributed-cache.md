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
- A buffer-capable distributed cache fills an owned entry buffer which the deserializer reads directly, without an
  additional full entry copy. The buffer remains valid across awaited metadata checks and is not returned to a pool.

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

Upgrade all instances that share a prefix together: older unconditional stamp writers do not obey this
contract. Timestamp metadata keeps its existing binary format. The HTTP response cache has its own
generation contract and still accepts a plain `IDistributedCache` for response storage.

Without a backplane, other instances keep their in-memory copy of an invalidated entry until it expires. Register an
`IBackplane` (in `MethodCache.Extensibility`) to send invalidations to them.

## Limits

- Pattern invalidation (`InvalidateByTagPatternAsync`) throws `NotSupportedException` with a distributed cache.
- [Fail-safe](fail-safe.md) values and [runtime overrides](configuration.md#runtime-overrides) live in each instance's
  memory.

## Package guides

SQL Server backplane publication, polling and retention cleanup are isolated by `KeyPrefix` even when
applications share the same tables. Scope matching is case-sensitive and independent of SQL collation.
Rows of other prefixes don't cost a poll extra queries: identity values they take are known to be committed,
so only values no row holds are watched for a late commit. Cleanup also removes unscoped rows from an earlier
build once they are older than `BackplaneRetention`; rows of a prefix no application uses any more are left
for you to delete.
Failed SQL Server backplane callbacks are retried before their rows are acknowledged, including late gap
messages and recovery clears; one that still fails after five polls is replaced by a clear of that instance's
memory, so later messages aren't held back. See the SQL Server guide for the schema migration required by an existing
invalidations table.

The [Redis](../../src/MethodCache.Redis/README.md) and [SQL Server](../../src/MethodCache.SqlServer/README.md) READMEs
cover their options, shared connections, table creation and outage settings.

SQL Server stores cache IDs as `mc:` followed by a SHA-256 hash of the exact UTF-16 key. This keeps
trailing spaces, NULs and different Unicode representations distinct despite SQL collation, and
gives keys and tags of any length a fixed-size ID. On upgrade, old unhashed cache rows become misses
and expire normally; upgrade all instances sharing a prefix together.

## Behavior and compatibility

L2 entries expire before their tag/clear markers can be forgotten. Their maximum shared lifetime is the effective `TagRetention - ClockSkewTolerance` measured from the source read, so slow loads reduce the remaining L2 lifetime; a load that exceeds this window stays in its loading node only.

Distributed tags and backplane keys/tags preserve exact UTF-16 code units, including lone surrogates. Envelope version 4 and backplane frames use lossless representations; older envelopes are cache misses. Deploy the same MethodCache version across nodes sharing a cache and backplane.

Redis escapes unpaired UTF-16 surrogates and backslashes in physical keys, keeping distinct logical keys separate in every data and invalidation operation. Ordinary keys and their prefixes stay unchanged.

Backplane invalidations do not cross wire versions in either direction. Deploy the same MethodCache version on all nodes sharing a cache/backplane; a rolling upgrade needs a new cache namespace or a coordinated cache clear. New message type numbers prevent legacy keys from being mistaken for encoded frames. Rejected frames emit one warning per backplane instance without logging their contents.

`TagRetention` must exceed `ClockSkewTolerance`; invalid settings fail startup or direct layer construction. L2 entries live at most `TagRetention - ClockSkewTolerance` from their source read, capped further by their policy duration.

L2 timestamps have millisecond precision: `TagRetention` and `ClockSkewTolerance` are each truncated to whole milliseconds before validation and lifetime calculation. Their effective difference must be at least one millisecond; settings that collapse to zero fail startup or direct construction.
