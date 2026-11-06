[← MethodCache documentation](../README.md)

# Distributed cache reference

The exact behavior of the Redis, SQL Server and PostgreSQL providers, for advanced users and coding agents. Start with
[Distributed cache](../features/distributed-cache.md) for how to use them.

## Which calls use the distributed cache

- `[Cache]` methods that return `Task<T>` or `ValueTask<T>`, and `GetOrCreateAsync`, `GetOrCreateManyAsync` and
  `TryGetAsync` on `ICacheManager`.
- **Synchronous `[Cache]` methods use the memory cache only.** None of the three providers has a synchronous API that
  can be bounded by `OperationTimeout`, so a synchronous method never blocks on the network. MethodCache logs this
  once per method. A synchronous `[CacheInvalidate]` method clears this instance's memory before it returns and stores
  the invalidation in the distributed cache and tells the other instances in the background; use an async method to
  wait for that.
- `[Cache(Distributed = false)]`, `"Distributed": false` in `appsettings.json` and `WithDistributed(false)` keep a
  method or call in memory only.

## Policies and shared entries

- Instances share a method's entries when they run it with the same settings from the attribute, startup code and
  `appsettings.json`. A runtime override that changes how a method caches (`IRuntimeCacheConfigurator`) gives that
  method entries of its own on that instance, which no other instance reads, even one with an equal override.
  Removing the override invalidates the method before the instance reads shared entries again.
- A key from `IMethodCacheKeys.For` or a manual key invalidates the entry under every policy.
- Invalidating a method (`ICacheAdministrator.InvalidateMethodAsync`) on one instance removes it on the others for
  every keyed registration and every closed generic type, without changing their settings.
- A value loaded before an invalidation is never stored after it, also when the write lands after a timeout and retry.
- Each entry remembers when its load started and when it expires. Reading it applies the reading instance's
  `CacheNullValues` and `CacheWhen`, and never keeps it longer than its own `Duration` (or the duration its
  `DurationFor` gives the value) from when it was first stored, or `MaxExpiration`. A slow method doesn't use up the entry's duration.

## Backplanes

- **Redis** sends each invalidation with pub/sub, which reaches the instances connected at that moment. After a lost
  connection, an instance clears its memory cache once it is subscribed again. Reconnecting retries with a delay that
  doubles from one second to 30 seconds; the health check reports connected only once the subscription is confirmed.
  Shutdown stops pending retries, and a cancelled send or unsubscribe never fails shutdown.
- **SQL Server** stores invalidations in a table that each instance polls (`BackplanePollingInterval`, one second).
  Identity values are handed out before a transaction commits, so a row can appear after later ones were read; an
  instance checks the ids it skipped again, without waiting on locks, so a long transaction never holds back newer
  invalidations. The table needs a `binary(32) NOT NULL` `Scope` column
  ([SQL Server guide](../../src/MethodCache.SqlServer/README.md#required-backplane-schema)).
- **PostgreSQL** stores invalidations in a table as SQL Server does, and announces each one with `NOTIFY`, so the
  others read it at once. Ids are handed out in commit order, so there are no gaps to check. The listening connection
  reconnects with the same doubling delay and reads what it missed; until then polling delivers every invalidation.
- With `EnableBackplane = false`, another instance keeps an invalidated value in memory until it expires.
- Disposing the service provider unsubscribes. A backplane that doesn't answer delays shutdown by at most twice
  `OperationTimeout`.
- A message from another MethodCache version is ignored, with one warning. A damaged message makes the instance clear
  its memory cache, with one warning that leaves out the message's contents.

## Invalidation results

`InvalidateKeysAsync` and `InvalidateTagsAsync` check the whole batch first, and count each distinct key or tag once:

- `Requested` and `Local` count every target, also ones that weren't cached.
- With a distributed cache, `Distributed` (stored) plus `Queued` (to be retried) equals `Requested`.
- With a backplane, `Published` counts sent messages and `PublicationQueued` the stored targets whose message will be
  sent again. A sent message doesn't confirm that every instance received it.

## Cancellation

- Cancelling a call stops its wait while it reads the distributed cache, runs a slow method or waits for a fail-safe
  value, also in bulk reads. Work other callers share may finish for them. A cancelled call is not counted as a
  failure of the distributed cache.
- Cancelling an invalidation before it starts changes nothing. Once it has started, cancelling stops the caller's wait;
  the cache finishes the invalidation and retries what it couldn't store. Storage and message retries have separate,
  bounded queues, which `ICacheAdministrator.GetInvalidationQueues()` and the health check report.

## Serialization

- Every value an instance writes is first read back and written again. When that doesn't give the same bytes and type,
  that value and every later value of that method and type stay in memory only, and one warning names them. Each
  value is checked, since one with default data (an `Id` of 0 behind a private setter) can come back unchanged where
  the next one doesn't.
- That happens with properties that have a private or protected setter, public readonly fields of a type that also
  has a parameterless constructor, a derived type returned as its declared base type (an `Animal` method returning a
  `Dog`), `object`, and values the serializer throws on (a cycle, a delegate), with JSON and MessagePack alike.
- State the serializer never writes (data only in private fields, which JSON writes as `{}`) reads back as the same
  bytes, so the check can't see that it is lost. The check also looks only at the value's own type: a `Dog` inside a
  `List<Animal>` is shared without its `Dog` data. Make such state serializable, or register a serializer that writes
  it, with `UseJson(options)` or `UseJson(context)` (`[JsonInclude]`, `[JsonDerivedType]`).
- A collection declared as an interface (`IReadOnlyList<int>`) may come back as another implementation, such as a
  `List<int>` for an array, and is still shared.
- A serializer that never writes the same bytes twice (encryption with a fresh nonce) can't be compared, so only the
  type is checked. `VerifyRoundTrip = false` turns the check off.
- The options you pass to `UseJson` are copied, so changing them later has no effect.

## Storage, schemas and compatibility

- `KeyPrefix` and the connection settings are fixed when the container is built; use a new container to change them.
- The first instance that uses a prefix fixes its minimum `TagRetention`. A later instance may ask for longer, not
  shorter; a shorter setting fails at startup. Use a new prefix to lower it.
- Entries are stored per value format and `SerializerVersion`, so switching between JSON and MessagePack, or raising
  `SerializerVersion`, makes old entries misses. Invalidations apply to every format.
- An entry's expiry is fixed from when its load started, so a slow write can't extend it.
- SQL Server and PostgreSQL create their tables on first use (`CreateTablesIfMissing`), and refuse a schema they don't
  know instead of changing it. SQL Server ships `migrations/0001-version-existing-schema.sql` for an invalidations
  table from before 1.0: stop the old instances, back up the database and run it before starting the new version.
  PostgreSQL ships `migrations/0001-create-schema.sql` for logins that may not create tables.
- `ConnectionStringFactory` is resolved once from dependency injection. PostgreSQL's `ConfigureDataSource` logs in
  with a token from Microsoft Entra ID or AWS IAM
  ([Passwordless login](../../src/MethodCache.PostgreSql/README.md#passwordless-login)).
- How each provider stores its data and messages is described for contributors in
  [the storage format](../developer/distributed-wire-v1.md).
