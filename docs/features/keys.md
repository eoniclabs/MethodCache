# Cache keys

You never write keys for `[Cache]` methods. The generated decorator builds each key from the interface, the method
and the arguments, so two calls share an entry exactly when they call the same method with equal arguments.

<!-- snippet: keys-arguments -->
```cs
public sealed record UserQuery(string Name, int Page);

public interface IUserService
{
    [Cache] Task<User> GetAsync(int id);                     // MyApp.IUserService.GetAsync:42
    [Cache] Task<List<User>> SearchAsync(UserQuery query);   // MyApp.IUserService.SearchAsync:(alice:2)
    [Cache] Task<List<User>> ByIdsAsync(int[] ids);          // MyApp.IUserService.ByIdsAsync:3f2a… (hashed)
}
```
<!-- endSnippet -->

## How arguments become keys

- **Simple values** (strings, numbers, `bool`, `Guid`, dates, enums) are written as they are: `GetAsync:42`.
- **Records, classes and structs** whose public properties and fields are simple values are written field by field:
  `SearchAsync:(alice:2)`. Two records with equal values give the same key.
- **Types that implement `ICacheKeyProvider`** contribute their `CacheKeyPart`.
- **Everything else**, such as arrays and collections, goes through the key generator. The default,
  `FastHashKeyGenerator`, hashes the arguments into a key such as `ByIdsAsync:11b474c4…`.
- **Values inside hashed arguments keep their runtime type.** Where a value's type differs from the type it is declared
  as (an element of `object[]` or `List<object>`, an `object` property, a property typed as a base class or interface),
  the key records the value's type and all of its members: `new object[] { 1 }` and `new object[] { 1L }` get
  different keys, and so do two `Circle`s behind a `Shape` property that differ only in `Radius`.
- A `CancellationToken` parameter is never part of the key.
- Keys don't depend on the current culture: `-1`, `1.5` and dates are written the same under every culture.

On .NET 9 and later the decorator writes simple keys into a stack buffer and looks them up without allocating.

## Types whose state is private

A type with only private state would give every value the same key, so the key generators refuse it with an
`InvalidOperationException` instead of returning wrong results. Implement `ICacheKeyProvider` on it:

<!-- snippet: keys-provider -->
```cs
public sealed class TenantId(Guid value) : ICacheKeyProvider
{
    private readonly Guid _value = value;   // private state: MethodCache can't see it

    public string CacheKeyPart => _value.ToString("N");
}
```
<!-- endSnippet -->

## Readable keys while debugging

`JsonKeyGenerator` writes the arguments it handles as JSON (for example `[42,"alice"]`) instead of a hash:

<!-- snippet: keys-json-generator -->
```cs
builder.Services.AddMethodCache();
builder.Services.Replace(ServiceDescriptor.Singleton<ICacheKeyGenerator, JsonKeyGenerator>());
```
<!-- endSnippet -->

The JSON is followed by a short hash of the arguments' runtime types (`[42,"alice"]:3f2a…`), so arguments that
write the same JSON, such as `1` and `1L` or two classes with the same properties, still get different keys. A value
inside an argument whose type differs from its declared type is written with its type, for example
`[{"\uFFFFt":"System.Int64","v":1}]` for `new object[] { 1L }`.

Both generators are in `MethodCache.Extensibility`. Keys built by the decorator itself (simple values, records,
`ICacheKeyProvider`) don't change with the generator.

## Building a key yourself

`MethodCacheKeys` builds the same key the decorator uses, so you can invalidate or inspect one entry without guessing
the format:

<!-- snippet: invalidation-by-key -->
```cs
var key = MethodCacheKeys.For(typeof(IOrderService), nameof(IOrderService.GetAsync), MethodCacheKeys.Part(42));
await cache.InvalidateByKeysAsync(key);   // "Docs.Invalidation.IOrderService.GetAsync:42"
```
<!-- endSnippet -->

## Things to know

- **Keys contain the arguments.** Don't cache methods that take secrets such as passwords or tokens as arguments; pass
  an identifier instead. Hashed keys are not encryption. No argument, whatever characters it contains, can produce
  another call's key; see [Security](../security.md).
- **Keys from the key generators changed** for arguments that hold values in `object`, base-class or interface slots,
  when the runtime types above became part of the key. Entries cached under the earlier keys are not found again and
  expire on their own.
- **`Version` changes every key** of a method, so values cached by an earlier version are ignored. See
  [Expiration](expiration.md#versions).
- To see the keys your application produces, list them with `ICacheAdministrator.ListEntries()` or the admin
  endpoints ([Monitoring](monitoring.md)).
