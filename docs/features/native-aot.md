# Native AOT

MethodCache works in Native AOT and trimmed applications. The source generator writes the decorators, their
registration and the cache keys at build time, so nothing is found or built with reflection at runtime. CI publishes a
test application with Native AOT on every change and checks that caching, the automatic registration and
`[assembly: CacheInterface]` work.

## In-memory caching

Nothing to do: the in-memory cache stores your objects as they are, without serializing them.

## Distributed caching

Values written to Redis or SQL Server are serialized with System.Text.Json. Under Native AOT it needs a
`JsonSerializerContext` that lists every type your cached methods return:

<!-- snippet: native-aot-context -->
```cs
[JsonSerializable(typeof(Product))]
[JsonSerializable(typeof(List<Product>))]   // every type a cached method returns
public sealed partial class ShopJsonContext : JsonSerializerContext;
```
<!-- endSnippet -->

<!-- snippet: native-aot-registration -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");
builder.Services.AddSingleton<ISerializer>(new JsonCacheSerializer(ShopJsonContext.Default.Options));
```
<!-- endSnippet -->

`ISerializer` and `JsonCacheSerializer` are in `MethodCache.Extensibility`. MethodCache can't generate this context
for you, because one source generator can't see another generator's output.

## Keys

Keys built by the decorator (simple values, records, `ICacheKeyProvider`) need no reflection. Arguments that go
through the key generator, such as collections, are hashed by `FastHashKeyGenerator`. See [Cache keys](keys.md).
