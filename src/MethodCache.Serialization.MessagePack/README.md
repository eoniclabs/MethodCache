# MethodCache.Serialization.MessagePack

Requires .NET 10 and the .NET 10 SDK; use the same package version as `MethodCache`.

Install this package and `MethodCache` at the exact same version. Integration packages use Core internals;
do not override their exact Core dependency. Our package smoke tests promote NU1608 to a restore error.

Stores MethodCache's distributed cache values (Redis or SQL Server) as compact MessagePack
instead of the default System.Text.Json: smaller entries and faster reads.

## Install

```bash
dotnet add package MethodCache.Serialization.MessagePack
```

## Register

<!-- snippet: distributed-messagepack -->
```cs
builder.Services.AddMethodCache(mc => mc
    .UseRedis("localhost:6379")
    .UseMessagePack());   // MethodCache.Serialization.MessagePack
```
<!-- endSnippet -->

Call `UseMessagePack()` with `UseRedis(...)` or `UseSqlServer(...)`, in any order; without either it does nothing and
logs a warning at startup. Entries written in the other format read as misses and are replaced, so switching needs
no flush.

## When to use it

- Values are large or read often from the distributed cache. It has no effect on the in-memory cache, which stores
  your objects as they are.
- Types serialize without attributes (contractless resolver). The defaults use LZ4 compression, keep `DateTime`
  values as they are, and read stored data with `MessagePackSecurity.UntrustedData`.
- Like JSON, the contractless resolver doesn't restore private setters or a derived type returned as its base type.
  With the default `VerifyRoundTrip = true`, MethodCache reads every value back before sharing it, and keeps values that don't survive that in memory only,
  with one warning per method and type.
- To change them, start from MethodCache's defaults: `UseMessagePack(o => o.WithCompression(MessagePackCompression.None))`
  gets the defaults above and returns the options to use, so the rest stays as it was. Keep `UntrustedData` and
  don't enable typeless payloads: the distributed cache must not choose which types get created
  (see [Security](https://github.com/eoniclabs/MethodCache/blob/main/docs/security.md#distributed-cache-redis-sql-server)).
- `UseMessagePack` changes how values are stored, never cache keys. Use `ICacheKeyProvider` for custom argument parts.

The check cannot detect lost nested derived/private state that was absent from the first serialized
representation. Configure the serializer for your model. Increment method `Version` or
`DistributedCacheOptions.SerializerVersion` when changing the representation.
