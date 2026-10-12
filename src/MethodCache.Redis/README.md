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

MethodCache keeps its own cache and connection, so your app's `IDistributedCache` and Redis settings are left alone. The connection opens on first use, so the application starts while Redis is down. It uses `BacklogPolicy.FailFast`, so a cache miss runs your method instead of waiting for Redis.

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
- Values are JSON written with System.Text.Json; add `MethodCache.Serialization.MessagePack` and call `AddMethodCacheMessagePack()` for MessagePack. Each entry records its format, so after a switch older entries count as misses.
- MethodCache reads Redis on an in-memory miss and writes new results to both.
- Invalidating a key removes it from Redis. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag sets are kept in Redis.
- Invalidation times are stored atomically, so an older invalidation that arrives late never undoes a newer one. Upgrade every instance that shares a `KeyPrefix` together, since earlier builds don't.
- Both also publish a message, so every instance drops its in-memory copies at once.
- Pub/sub delivers nothing while a connection is down. When the connection comes back, the instance clears its in-memory cache and reads entries from Redis again.
- Pattern invalidation (`InvalidateByTagPatternAsync`) is not supported with a distributed cache and throws `NotSupportedException`.

## When Redis is down

MethodCache keeps answering from memory and your methods; Redis never fails or stalls a call.

- Every Redis call is capped at `OperationTimeout` (1 second).
- After a failure or timeout, MethodCache skips Redis for `CircuitBreakerDuration` (10 seconds), then one call checks whether it is back. The first failure and the recovery are logged.
- Invalidations that Redis doesn't accept are queued and retried until it does. They never throw. Until they are stored, this instance doesn't read entries from Redis, so an invalidated value can't come back.
- A value set directly while Redis is down stays in this instance's memory; the value it replaced is removed from Redis and the other instances once Redis is back.
- `ClockSkewTolerance` (2 seconds) covers servers whose clocks differ: a tag invalidation also applies to entries created up to that long after it.

<!-- snippet: distributed-outage-options -->
```cs
builder.Services.Configure<DistributedCacheOptions>(options =>
{
    options.OperationTimeout = TimeSpan.FromMilliseconds(500);
    options.CircuitBreakerDuration = TimeSpan.FromSeconds(30);
});
```
<!-- endSnippet -->

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `Configuration` | `localhost:6379` | StackExchange.Redis configuration string |
| `ConnectionMultiplexerFactory` | none | Returns an existing connection to share instead of opening one |
| `KeyPrefix` | `methodcache:` | Prefix for every key MethodCache writes |
| `EnableBackplane` | `true` | Send invalidations to other instances over pub/sub |
| `BackplaneChannel` | `KeyPrefix` + `invalidations` | Pub/sub channel; applications with different key prefixes get different channels |

Distributed tags and backplane keys/tags preserve exact UTF-16 code units, including lone surrogates. Envelope version 4 and backplane frames use lossless representations; older envelopes are cache misses. Deploy the same MethodCache version across nodes sharing a cache and backplane.

Redis escapes unpaired UTF-16 surrogates and backslashes in physical keys, keeping distinct logical keys separate in every data and invalidation operation. Ordinary keys and their prefixes stay unchanged.
