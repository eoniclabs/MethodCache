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

Redis backplane publication stops waiting when its token is cancelled. A cancelled unsubscribe acknowledgement does not fail application shutdown. SQL backplane identity gaps are not forgotten just because a transaction took longer than five seconds: committed gap rows are read without waiting, and a separate `NOWAIT` locking probe confirms an older identity is absent before it is forgotten. A locked or timed-out probe leaves gaps available for retry while newer invalidations and other committed gap messages are delivered.

## How it works

- Results live in memory (L1) and in the distributed cache (L2). An in-memory miss reads the distributed cache before
  running your method, and a new result is written to both.
- Invalidating a key removes it from the distributed cache. Invalidating a tag records when it happened, so older
  entries with that tag read as misses. A backplane tells every other instance to drop its in-memory copy: at once
  with Redis pub/sub, or on their next poll (every second by default) with SQL Server.
- Only Redis and Microsoft SQL Server have packages. Another store, such as PostgreSQL, needs a
  [owned storage](#owned-storage-and-compatibility).
- `[Cache]` methods and `GetOrCreateAsync` need no changes.
- MethodCache keeps its own distributed cache and connection, so your application's `IDistributedCache` is left alone.

Run the same MethodCache version on every instance that shares a cache and backplane; messages from another version
are ignored with one warning.

## Policy isolation

Declared methods with equal effective startup/configuration options share a stable L2 namespace. A behavior-changing
runtime override uses a private namespace for that method and container. Equal overrides on different nodes cannot
share private values. Removing an override invalidates the declared method before allowing shared L2 reads again.
The logical key from `MethodCacheKeys` remains suitable for invalidation across policy variants.

A received method invalidation retires data for all closed generic and keyed registrations, without changing the
receiver's policy. Dissemination is best effort: a missed message can leave remote L1 values until expiry or reconnect.
L2 metadata rejects values loaded before invalidation, including writes that land after a timeout and retry.

Entries retain load start, first publication time and an absolute deadline. L2 reads apply the receiver's null policy
and result predicate, and cap remaining lifetime by the stored deadline, its own duration from first publication and
`MaxExpiration`. A slow factory does not consume the value's duration; it reduces the remaining metadata-retention margin.
Synchronous methods currently use L1 and the origin when these providers cannot offer a bounded native synchronous
operation. This emits a diagnostic once per method/container. Synchronous invalidation retires local data immediately
and leaves L2 recovery owned by the cache.

## Invalidation results

`InvalidateKeysAsync` and `InvalidateTagsAsync` validate the full batch and count distinct targets. `Requested` and
`Local` count targets, including local no-ops. `Distributed` counts confirmed L2 metadata; `Queued` counts unconfirmed
L2 targets. With L2 configured, their sum equals `Requested`. `Published` counts backplane sends and
`PublicationQueued` counts confirmed L2 targets whose sends need retry. A send does not confirm receipt by every node.
There are no distributed counters without L2 and no publication counters without a backplane.

Cancellation before mutation leaves data untouched. Afterwards it cancels the caller's wait while owned recovery
continues. Storage and publication have separate bounded queues, exposed through administration and health checks.

## When the distributed cache is down

Calls keep working from memory and your methods; the distributed cache never fails or stalls a call. Each call to it
is capped at `OperationTimeout` (one second), a circuit breaker skips it until it is back, and invalidations are queued and retried
instead of throwing. Until they are stored, the instance doesn't read entries from it, so an invalidated value can't
come back. The [health check](monitoring.md#health-check) reports Degraded while this lasts.

Queued invalidations are stored a batch at a time once the cache answers, so a long outage's backlog drains at the
pace the cache can take. More than 10,000 are replaced by one clear of the distributed cache (logged as a warning),
and so is an invalidation the cache keeps refusing while it answers everything else, so neither holds up the rest.
The queue lives in memory: when the instance shuts down, it waits for writes still running, then tries once more to store
what is queued, each step (stamp, removal, publish) capped by `OperationTimeout`, and logs a warning with the number it had to drop. Other instances can read those invalidated
values from the distributed cache until they expire.

A value set directly while the distributed cache is down stays in that instance's memory, and the value it replaced is
removed from the distributed cache and the other instances once it is back. An entry that comes back damaged (cut
short, or unreadable by the serializer) is a miss: your method runs and its result replaces the entry.

The timeout caps each call to the distributed cache, not a whole cache miss. A cache that stops answering costs a miss
one timeout, after which the circuit breaker skips it. One that answers just within the timeout costs a miss up to
three: reading the entry, writing your method's result and checking that no removal raced the write (and a fourth for
the backplane message).

## Options

`DistributedCacheOptions` apply to the owned Redis and SQL Server providers:

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
| `KeyPrefix` | `methodcache:` | One startup-immutable prefix for values, metadata, HTTP epochs and the backplane; give each application its own |

| `VerifyRoundTrip` | `true` | Verify the serialized representation before publishing; not a proof of semantic equality |
| `SerializerVersion` | `1` | Positive value-format identity; increment when changing serializer settings |

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
are shared as they are. Every value an instance writes is read back and written again first; when that doesn't give
the same bytes and type, that value and every later value of the method with that type stay in the instance's memory
only, and one warning names the method and the type. Each value is checked, since one with default data (an `Id` of
0 behind a private setter) can come back unchanged where the next one doesn't. A serializer that never writes the same
bytes twice (encryption with a fresh nonce) can't be compared that way, so for it only the type is checked. State
the serializer never writes at all (a class that keeps its data only in private fields, which JSON writes as `{}`)
reads back as the same bytes, so the check can't see that it is lost: make such state serializable, or register a
serializer that writes it. That happens with properties that have a
private or protected setter, public readonly fields of a type that also has a parameterless constructor, a derived
type returned as its declared base type (an `Animal` method returning a `Dog`, even after it returned plain
`Animal`s), and `object`, with JSON and MessagePack alike. A value the serializer throws on (a cycle, a delegate
field) stays in memory the same way. A collection declared as an interface (`IReadOnlyList<int>`,
`IEnumerable<int>`) may come back as another implementation, a `List<int>` for an array, and is still shared. The
check looks at the type of the value itself, not of what it holds: a `Dog` inside a `List<Animal>` or in a property
declared `Animal` is shared without its `Dog` data. To share such values, register a serializer that reads them back,
through `UseJson` with your own `JsonSerializerOptions` or `JsonSerializerContext` (`[JsonInclude]` on the
setter, `[JsonDerivedType]` on the base type). The `MethodCache.Serialization.MessagePack` package makes values
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

## Owned storage and compatibility

Use `MethodCacheBuilder.UseRedis` or `UseSqlServer`. Storage, backplane, atomic metadata and serializers are internal owned services; the application's `IDistributedCache` is never a fallback. Configure `DistributedCacheOptions.KeyPrefix` before building the container. All Configure registrations participate, including those registered after provider selection. The bound prefix and connection settings are immutable; use a new container to change them.

`TagRetention` establishes a permanent scope minimum on first use. Later nodes may request at least that minimum; a shorter setting is rejected. Value deadlines remain capped by the established minimum, even when a later node requests longer retention. Use a new prefix to establish a different scope contract. Exact key removals retain tombstones for metadata retention across every entry format and policy identity. Writes use absolute physical deadlines anchored to source start, so slow serialization or transport cannot extend them.

Entry keys include the entry format and `SerializerVersion` (default 1); logical invalidation metadata does not. Raise the method `Version` or `DistributedCacheOptions.SerializerVersion` when changing result schemas or serializer settings. Content type only identifies the serialization family. Wire contract 1 frames key, application-tag, method and clear as separate operations. Unknown optional fields are ignored; incompatible mandatory frames retire local entries and produce one payload-free warning.

SQL Server creates only fresh schema contract 1 automatically. Existing unversioned or incompatible schema fails explicitly. The package includes `migrations/0001-version-existing-schema.sql`; stop all old nodes, back up the database and apply the supported migration before starting the new version. `CreateTablesIfMissing = false` prevents creation while retaining schema validation. `SqlServerOptions.ConnectionStringFactory` resolves once from DI.

Select JSON with `UseJson(options)` or `UseJson(context)`, or MessagePack with `UseMessagePack`. JSON options are copied and frozen. `DistributedCacheOptions.VerifyRoundTrip` defaults to true and can be disabled explicitly. The check cannot prove semantic equality: a nested derived value may lose fields during its first serialization without changing the outer runtime type or reserialized bytes. Configure polymorphic metadata/source-generated contexts when necessary. `WithDistributed(false)` skips method L2 reads and writes.

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

- Invalidation matches exact keys and tags; common tags provide group invalidation.
- [Fail-safe](fail-safe.md) values and [runtime overrides](configuration.md#runtime-overrides) live in each instance's
  memory.

## Package guides

The [Redis](../../src/MethodCache.Redis/README.md) and [SQL Server](../../src/MethodCache.SqlServer/README.md) READMEs
cover their options, shared connections, table creation, outage settings and how each stores keys and invalidations.
The SQL Server guide also compares the two and has the schema migration for an existing invalidations table.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. Shared work may finish for other callers; cancellation is not reported as a dependency failure.

SQL Server backplane initialization requires a non-nullable `binary(32)` `Scope` column. Incompatible scope columns
are reported without automatic schema upgrades; see the [SQL Server guide](../../src/MethodCache.SqlServer/README.md#required-backplane-schema).
