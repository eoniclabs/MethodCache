# MethodCache.Providers.Redis

Redis as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache over Redis pub/sub.

## Usage

```csharp
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");
```

With options:

```csharp
builder.Services.AddMethodCacheRedis(options =>
{
    options.Configuration = "redis1:6379,redis2:6379,password=...";
    options.KeyPrefix = "orders-api:";
    options.BackplaneChannel = "orders-api:invalidations";
});
```

An `IConnectionMultiplexer` you have already registered is reused for both the cache and the backplane. The one MethodCache creates otherwise uses `AbortOnConnectFail = false`, so the application starts while Redis is down, and `BacklogPolicy.FailFast`, so a cache miss runs your method instead of waiting for Redis.

## How it works

- Entries are stored through `Microsoft.Extensions.Caching.StackExchangeRedis`, so values are written without extra copies.
- Values are JSON written with System.Text.Json; add `MethodCache.Serialization.MessagePack` and call `AddMethodCacheMessagePack()` for MessagePack. Each entry records its format, so after a switch older entries count as misses.
- MethodCache reads Redis on an in-memory miss and writes new results to both.
- Invalidating a key removes it from Redis. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag sets are kept in Redis.
- Both also publish a message, so every instance drops its in-memory copies at once.
- Pub/sub delivers nothing while a connection is down. When the connection comes back, the instance clears its in-memory cache and reads entries from Redis again.
- Invalidating while Redis is unreachable throws, so you know the other instances were not told.
- Pattern invalidation (`InvalidateByTagPatternAsync`) is not supported with a distributed cache and throws `NotSupportedException`.

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `Configuration` | `localhost:6379` | StackExchange.Redis configuration string |
| `KeyPrefix` | `methodcache:` | Prefix for every key MethodCache writes |
| `EnableBackplane` | `true` | Send invalidations to other instances over pub/sub |
| `BackplaneChannel` | `methodcache:invalidations` | Pub/sub channel; applications that share Redis but not data need different channels |
