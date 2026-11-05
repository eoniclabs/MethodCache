# MethodCache.Redis

Redis as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach
every instance's in-memory cache over Redis pub/sub.

Install it at the same version as `MethodCache`.

## Usage

<!-- snippet: redis-basic -->
```cs
builder.Services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));
```
<!-- endSnippet -->

With options:

<!-- snippet: redis-options -->
```cs
builder.Services.AddMethodCache(mc => mc
    .UseRedis(options => options.Configuration = "redis1:6379,redis2:6379,password=...")
    .Distributed(options => options.KeyPrefix = "orders-api:"));   // data and backplane use the same final prefix
```
<!-- endSnippet -->

To share a connection your app already has instead:

<!-- snippet: redis-shared-connection -->
```cs
builder.Services.AddMethodCache(mc => mc.UseRedis(options =>
    options.ConnectionMultiplexerFactory = sp => sp.GetRequiredService<IConnectionMultiplexer>()));
```
<!-- endSnippet -->

- Set `Configuration` or `ConnectionMultiplexerFactory`. Startup fails when neither is set, so a missing configuration
  section never makes MethodCache connect to `localhost`.
- MethodCache keeps its own cache and connection, so your app's `IDistributedCache` and Redis settings are left alone.
- The connection opens on first use, on the thread pool, so the application starts while Redis is down and a slow
  connect never holds up a call.
- MethodCache doesn't dispose a shared connection. Disposing the service provider removes MethodCache's own
  subscriptions.

### Invalidations only

When each process should keep its own values and only hear about invalidations, such as a worker that changes data an
API caches, turn the distributed cache off for every method, in every process. Values stay in each process's memory,
none are written to Redis, and an invalidation in one process reaches all of them through pub/sub:

<!-- snippet: redis-invalidations-only -->
```cs
builder.Services.AddMethodCache(mc => mc
    .UseRedis("localhost:6379")
    .Defaults(o => o.WithDistributed(false)));   // values stay in memory; invalidations reach every process
```
<!-- endSnippet -->

See [Invalidations only](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#invalidations-only).

## How it works

- MethodCache reads Redis on an in-memory miss and writes new results to both.
- Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as
  missing, so no tag sets are kept in Redis. An older invalidation that arrives late never undoes a newer one.
- Every invalidation is also published on the `<prefix>invalidations` channel, so every instance drops its in-memory
  copies at once.
- Pub/sub delivers nothing while a connection is down. When the instance is subscribed again, it clears its in-memory
  cache. Reconnecting retries after 1 second, then twice as long each time, up to 30 seconds.

## Memory and eviction

Invalidations are stored as keys beside the entries. If Redis evicts one under memory pressure, the entries it
invalidated are served again until they expire. Set `maxmemory-policy noeviction` on the Redis MethodCache uses (or give
it a Redis of its own) and give it enough memory. The `volatile-*` policies, the default of some managed Redis
services, can evict them too. A full Redis with `noeviction` refuses writes, which MethodCache handles like an outage.
See
[Keep invalidations from being evicted](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#keep-invalidations-from-being-evicted).

## When Redis is down

MethodCache keeps answering from memory and your methods; Redis never fails or stalls a call. Each call is capped at
`OperationTimeout` (1 second), Redis is skipped for `CircuitBreakerDuration` (10 seconds) after a failure, and
invalidations are queued and retried instead of throwing. The first failure and the recovery are logged.

The [distributed cache guide](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md)
covers outages, consistency between instances, serialization and the `DistributedCacheOptions` that tune all of this.
The exact behavior is in the
[distributed cache reference](https://github.com/eoniclabs/MethodCache/blob/main/docs/reference/distributed.md).

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `Configuration` | none | StackExchange.Redis configuration string |
| `ConnectionMultiplexerFactory` | none | Returns an existing connection to share instead of opening one |
| `EnableBackplane` | `true` | Send invalidations to other instances over pub/sub |

`DistributedCacheOptions.KeyPrefix` applies to values, invalidations and the channel. The first instance that uses a
prefix fixes its minimum `TagRetention`; a shorter setting fails at startup, so use a new prefix to lower it.
