# Native AOT

MethodCache works in Native AOT and trimmed applications when your cached methods take arguments the decorator can
write into the key itself (see [Keys](#keys) below). The source generator writes the decorators, their registration
and those keys at build time. CI publishes a test application with Native AOT on every change and checks that
caching (with simple, record, collection and `ICacheKeyProvider` arguments), explicit `AutoRegister(ServiceLifetime)` registration and
`[assembly: CacheInterface]` work.

CI also compiles the complete supported signature corpus with Native AOT. This checks generated signatures and
ref safety across all key forms; reflection-based fallback cases retain the runtime limitations described below.

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
builder.Services.AddMethodCache(cache => cache.UseJson(ShopJsonContext.Default));
```
<!-- endSnippet -->

Select your context with `MethodCacheBuilder.UseJson(context)`. MethodCache cannot generate this context
for you, because one source generator can't see another generator's output.

## Keys

Under Native AOT, give cached methods arguments that the decorator writes into the key itself:

- simple values (strings, numbers, `bool`, `Guid`, dates, enums),
- records, classes and structs whose public properties and fields are simple values, passed as exactly the declared
  type (a sealed type is safest: an instance of a derived class goes through the key generator),
- types that implement `ICacheKeyProvider`,
- arrays, lists and sets of those (records and classes in them must be sealed), and dictionaries whose keys and values
  are simple values.

Collection subclasses with their own public state also use the key generator; implement `ICacheKeyProvider` on
these types when using Native AOT. Plain lists, arrays and sets keep their inline keys.

Other arguments, such as interfaces, `object` or collections of collections, go through the key generator, which
serializes them with reflection-based System.Text.Json. Publishing then reports trim and AOT warnings (IL2026,
IL3050) from `MethodCache.Core`, and the call throws `InvalidOperationException` at runtime. Wrap such an argument in a type that
implements `ICacheKeyProvider`, as `TenantId` does in [Cache keys](keys.md#types-whose-state-is-private).
