# MethodCache.Redis

Redis as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache over Redis pub/sub.

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
    options.KeyPrefix = "orders-api:";   // the backplane channel follows: orders-api:invalidations
});
```
<!-- endSnippet -->

MethodCache keeps its own cache and connection, so your app's `IDistributedCache` and Redis settings are left alone. The connection opens on first use, so the application starts while Redis is down. It uses `BacklogPolicy.FailFast`, so a cache miss runs your method instead of waiting for Redis. When a connection factory of yours throws (Redis was down at first use, say), the next call asks it again.

To share a connection your app already has instead:

<!-- snippet: redis-shared-connection -->
```cs
builder.Services.AddMethodCacheRedis(options =>
    options.ConnectionMultiplexerFactory = sp => sp.GetRequiredService<IConnectionMultiplexer>());
```
<!-- endSnippet -->

MethodCache doesn't dispose a shared connection; disposing the service provider only removes MethodCache's own
subscriptions (`DisposeAsync` waits for that).

## How it works

- Entries are stored through `Microsoft.Extensions.Caching.StackExchangeRedis`, so values are written without extra copies.
- MethodCache reads Redis on an in-memory miss and writes new results to both.
- Invalidating a key removes it from Redis. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag sets are kept in Redis.
- Invalidation times are written with one Lua script, so an older invalidation that arrives late never undoes a newer one.
- Every invalidation is also published on the backplane channel, so every instance drops its in-memory copies at once.
- Pub/sub delivers nothing while a connection is down. When the connection comes back, the instance clears its in-memory cache and reads entries from Redis again.
- Keys with unpaired UTF-16 surrogates or backslashes are escaped, so two different keys never share a Redis key. Other keys are stored unchanged.

## Memory and eviction

Invalidations are stored as expiring keys beside the entries. If Redis evicts one under memory pressure, the entries it
invalidated are served again by every instance until they expire. Set `maxmemory-policy noeviction` on the Redis
MethodCache uses (or give it a Redis of its own) and enough memory: every MethodCache key has a time to live, so
`volatile-lru` and the other `volatile-*` policies, the default of some managed Redis services, can evict them too. A
full Redis with `noeviction` refuses writes, which MethodCache handles like an outage. See
[Keep invalidations from being evicted](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#keep-invalidations-from-being-evicted).

## When Redis is down

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
| `KeyPrefix` | `methodcache:` | Prefix for every key MethodCache writes |
| `EnableBackplane` | `true` | Send invalidations to other instances over pub/sub |
| `BackplaneChannel` | `KeyPrefix` + `invalidations` | Pub/sub channel; applications with different key prefixes get different channels |
