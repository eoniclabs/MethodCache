# MethodCache.Serialization.MessagePack

Install this package and `MethodCache` at the exact same version. Integration packages use Core internals;
do not override their exact Core dependency. Our package smoke tests promote NU1608 to a restore error.

When using `MessagePackKeyGenerator`, collection subclasses include their own public properties and fields as well as their elements in the key. Collections with different scope or tenant state cannot share a cached result.

Stores MethodCache's distributed cache values (Redis, SQL Server or a custom distributed cache) as compact MessagePack
instead of the default System.Text.Json: smaller entries and faster reads.

## Install

```bash
dotnet add package MethodCache.Serialization.MessagePack
```

## Register

<!-- snippet: distributed-messagepack -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");
builder.Services.AddMethodCacheMessagePack();   // MethodCache.Serialization.MessagePack
```
<!-- endSnippet -->

Call `AddMethodCacheMessagePack()` with `AddMethodCacheRedis()`, `AddMethodCacheSqlServer()` or
`AddMethodCacheDistributedCache()`, in any order. Entries written in the other format read as misses and are replaced,
so switching needs no flush.

## When to use it

- Values are large or read often from the distributed cache. It has no effect on the in-memory cache, which stores
  your objects as they are.
- Types serialize without attributes (contractless resolver). The defaults use LZ4 compression, keep `DateTime`
  values as they are, and read stored data with `MessagePackSecurity.UntrustedData`.
- Like JSON, the contractless resolver doesn't restore private setters or a derived type returned as its base type.
  MethodCache reads every value back before sharing it, and keeps values that don't survive that in memory only,
  with one warning per method and type.
- Pass your own `MessagePackSerializerOptions` to `AddMethodCacheMessagePack(options)` to change them. Keep
  `UntrustedData` and don't enable typeless payloads: the distributed cache must not choose which types get created
  (see [Security](https://github.com/eoniclabs/MethodCache/blob/main/docs/security.md#distributed-cache-redis-sql-server)).

## Key generator (optional)

`MessagePackKeyGenerator` builds keys from a SHA-256 hash of the arguments' MessagePack form. Use it when your argument
types serialize with MessagePack but not with System.Text.Json, which the default key generator uses:

<!-- snippet: messagepack-key-generator -->
```cs
builder.Services.AddMethodCache();
builder.Services.Replace(ServiceDescriptor.Singleton<ICacheKeyGenerator, MessagePackKeyGenerator>());
```
<!-- endSnippet -->

Keys keep the usual form (`{method}:{hash}`), so invalidating all of a method's entries works the same way. They
include the arguments' assembly-qualified type names, so they change when those assemblies' versions change.

## More

The [distributed cache guide](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md)
covers how the distributed cache works, outages and options. [Cache keys](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/keys.md)
covers the other key generators.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

`IPAddress` and `JsonElement` arguments are written as their text (a `JsonElement` as its JSON), like the other key
generators do, instead of their properties; keys of such arguments change.
