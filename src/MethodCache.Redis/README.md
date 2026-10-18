# MethodCache.Redis

Requires .NET 10 and the .NET 10 SDK; use the same package version as `MethodCache`.

Install this package and `MethodCache` at the exact same version. Integration packages use Core internals;
do not override their exact Core dependency. Our package smoke tests promote NU1608 to a restore error.

Redis as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache over Redis pub/sub.

Backplane publication honors its cancellation token before sending and while waiting for a connection or Redis acknowledgement. Cancellation after a publish was sent cannot recall that message.

## Usage

<!-- snippet: redis-basic -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");
```
<!-- endSnippet -->

With options:

<!-- snippet: redis-options -->
```cs
builder.Services.AddMethodCacheRedis(options =>
{
    options.Configuration = "redis1:6379,redis2:6379,password=...";
});
builder.Services.Configure<DistributedCacheOptions>(options =>
    options.KeyPrefix = "orders-api:");   // data and backplane use the same final prefix
```
<!-- endSnippet -->

MethodCache keeps its own cache and connection, so your app's `IDistributedCache` and Redis settings are left alone. The connection opens on first use, so the application starts while Redis is down. It uses `BacklogPolicy.FailFast`, so a cache miss runs your method instead of waiting for Redis. When a connection factory of yours throws (Redis was down at first use, say), the next call asks it again, and the backplane keeps trying to connect and subscribe (after 1 second, then twice as long each time, up to 30 seconds), logging a warning for each failure, until it succeeds or the application stops. Once subscribed after a failure, it clears this instance's in-memory cache, as after a reconnect, since invalidations other instances sent meanwhile never arrived.

To share a connection your app already has instead:

<!-- snippet: redis-shared-connection -->
```cs
builder.Services.AddMethodCacheRedis(options =>
    options.ConnectionMultiplexerFactory = sp => sp.GetRequiredService<IConnectionMultiplexer>());
```
<!-- endSnippet -->

MethodCache doesn't dispose a shared connection; disposing the service provider only removes MethodCache's own
subscriptions (`DisposeAsync` waits for that). A cancelled Redis unsubscribe acknowledgement does not fail application shutdown; the handler has already been removed locally.

## How it works

- Entries are stored through `Microsoft.Extensions.Caching.StackExchangeRedis`, so values are written without extra copies.
- MethodCache reads Redis on an in-memory miss and writes new results to both.
- Invalidating a key writes a logical tombstone shared by all value formats and policy identities, then removes the current physical value. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag sets are kept in Redis.
- Invalidation times are written with one Lua script, so an older invalidation that arrives late never undoes a newer one.
- Every invalidation is also published on the backplane channel, so every instance drops its in-memory copies at once.
- Pub/sub delivers nothing while a connection is down. When the connection comes back, the instance clears its in-memory cache and reads entries from Redis again.
- Keys with unpaired UTF-16 surrogates or backslashes are escaped, so two different keys never share a Redis key. Other keys are stored unchanged.

## Memory and eviction

Invalidations are stored as expiring keys beside the entries. If Redis evicts one under memory pressure, the entries it
invalidated are served again by every instance until they expire. Set `maxmemory-policy noeviction` on the Redis
MethodCache uses (or give it a Redis of its own) and enough memory: method invalidation keys have a time to live; HTTP epochs and the scope-retention contract are permanent, so
`volatile-lru` and the other `volatile-*` policies, the default of some managed Redis services, can evict them too. A
full Redis with `noeviction` refuses writes, which MethodCache handles like an outage. See
[Keep invalidations from being evicted](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#keep-invalidations-from-being-evicted).

## When Redis is down

Redis backplane recovery retries transient unsubscribe/subscribe failures after reconnect, with a delay that doubles from one second to at most 30 seconds. It reports connected only after subscription acknowledgement and the local recovery clear; shutdown cancels pending retries.

MethodCache keeps answering from memory and your methods; Redis never fails or stalls a call. Each call is capped at
`OperationTimeout` (1 second), Redis is skipped for `CircuitBreakerDuration` (10 seconds) after a failure, and
invalidations are queued and retried instead of throwing. The first failure and the recovery are logged.

The [distributed cache guide](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md)
covers outages, consistency between instances, MessagePack serialization, limits and the `DistributedCacheOptions`
that tune all of this.

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `Configuration` | `localhost:6379` | StackExchange.Redis configuration string |
| `ConnectionMultiplexerFactory` | none | Returns an existing connection to share instead of opening one |
| `EnableBackplane` | `true` | Send invalidations to other instances over pub/sub |


Configure `DistributedCacheOptions.KeyPrefix` once for values, invalidations, permanent HTTP epochs and the
`<prefix>invalidations` channel. All Configure registrations are applied before the snapshot is frozen.
Wire v1 supports mixed entry formats; malformed mandatory frames clear local entries and warn once.
Value and logical tombstone are read in one pipeline. Atomic metadata updates never shorten retention.
The first node establishes the scope retention floor; a shorter setting is rejected, while longer settings
use that common bound. Use a new prefix to change it.
