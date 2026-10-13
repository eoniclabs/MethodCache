# Native AOT

MethodCache works in Native AOT and trimmed applications when your cached methods take arguments the decorator can
write into the key itself (see [Keys](#keys) below). The source generator writes the decorators, their registration
and those keys at build time. CI publishes a test application with Native AOT on every change and checks that
caching (with simple, record and `ICacheKeyProvider` arguments), the automatic registration and
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

Under Native AOT, give cached methods arguments that the decorator writes into the key itself:

- simple values (strings, numbers, `bool`, `Guid`, dates, enums),
- records, classes and structs whose public properties and fields are simple values, passed as exactly the declared
  type (a sealed type is safest: an instance of a derived class goes through the key generator),
- types that implement `ICacheKeyProvider`.

Other arguments, such as arrays, lists and dictionaries, go through the key generator, which serializes them with
reflection-based System.Text.Json. Publishing then reports trim and AOT warnings (IL2026, IL3050) from
`MethodCache.Core`, and the call throws `InvalidOperationException` at runtime. Wrap such an argument in a type that
implements `ICacheKeyProvider`, as `TenantId` does in [Cache keys](keys.md#types-whose-state-is-private).
