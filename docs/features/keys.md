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
    [Cache] Task<List<User>> ByIdsAsync(int[] ids);          // MyApp.IUserService.ByIdsAsync:[3:1:2:3]
    [Cache] Task<int> CountAsync(Dictionary<string, int[]> filters); // MyApp.IUserService.CountAsync:fc08ab0d… (hashed)
}
```
<!-- endSnippet -->

## How arguments become keys

- **Simple values** (strings, numbers, `bool`, `Guid`, dates, enums) are written as they are: `GetAsync:42`.
- **Records, classes and structs** whose public properties and fields are simple values are written field by field:
  `SearchAsync:(alice:2)`. Two records with equal values give the same key.
- **Types that implement `ICacheKeyProvider`** contribute their `CacheKeyPart`.
- **Arrays and collections** of those values are written with their count and then each element in order:
  `ByIdsAsync:[3:1:2:3]`. Records and classes inside a collection must be sealed, since an unsealed element type
  could hold a subclass with more members. A dictionary counts when its keys and values are simple values. When a
  collection's runtime type differs from its declared type (an array or a list passed as `IEnumerable<int>`), the key
  starts with that type, so an array and a list with the same elements stay two entries.
- **Everything else**, such as an interface or `object` argument or a collection of collections, goes through the key
  generator. The default, `FastHashKeyGenerator`, hashes the arguments into a key such as `CountAsync:fc08ab0d…`.
- **Framework types** are written by their value wherever they appear, not by their public properties; see
  [Framework types](#framework-types). Other framework types are a build error.
- **`nint` and `nuint`** also go through the key generator, as the 64-bit number they hold, so a value gets the same
  key in 32-bit and 64-bit processes. Their type is kept: `(nint)1`, `1` and `1L` are three keys, also inside an
  `object[]` or an `object` property.
- **Values inside hashed arguments keep their runtime type.** Where a value's type differs from the type it is declared
  as (an element of `object[]` or `List<object>`, an `object` property, a property typed as a base class or interface),
  the key records the value's type and all of its members: `new object[] { 1 }` and `new object[] { 1L }` get
  different keys, and so do two `Circle`s behind a `Shape` property that differ only in `Radius`.
- A `CancellationToken` parameter is never part of the key.
- Keys don't depend on the current culture: `-1`, `1.5` and dates are written the same under every culture.
- `0.0` and `-0.0` are two keys in every key form (`1 / x` tells them apart). `NaN` and the infinities work anywhere,
  also inside a hashed argument, such as `double.PositiveInfinity` as a "no limit" value in a filter record.

On .NET 9 and later the decorator writes simple keys into a stack buffer and looks them up without allocating.

## Types whose state is private

A type with only private state, including private fields inherited from a base class, would give every value the
same key, so the key generators refuse it with an
`InvalidOperationException` instead of returning wrong results. This holds wherever the type appears in an argument:
as the argument itself, as a property of a record, or as an element of an array, list or dictionary. Implement
`ICacheKeyProvider` on it, or give it public properties:

<!-- snippet: keys-provider -->
```cs
public sealed class TenantId(Guid value) : ICacheKeyProvider
{
    private readonly Guid _value = value;   // private state: MethodCache can't see it

    public string CacheKeyPart => _value.ToString("N");
}
```
<!-- endSnippet -->

## Framework types

Types from .NET itself often keep their value out of their public properties (`BigInteger` has `IsZero` and
`Sign`, `IPAddress.ScopeId` throws for IPv4), so writing those would give different values one key. These go through
the key generator, which writes their value, in an argument of their own or inside a record, collection or tuple:

- `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`
- `Half`, `Int128`, `UInt128`, `BigInteger`, `Complex`
- `Uri`, `Version`, `IPAddress` (`IPAddress.Loopback` and `IPAddress.Parse("127.0.0.1")` share a key)
- `JsonElement`, as its JSON, so `{"a":1}` and `{ "a": 1 }` share a key
- `Index`, `Range`, `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Plane`, `Matrix3x2`, `Matrix4x4`
- ASP.NET Core's `PathString`, `QueryString` and `HostString`
- arrays, collections, `Memory<T>`, `Tuple`, `ValueTuple` and `KeyValuePair` of these

Any other type from .NET itself, ASP.NET Core or Microsoft.Extensions in a key (one from an assembly named
`System.*`, `Microsoft.AspNetCore.*` or `Microsoft.Extensions.*`), such as `StringBuilder` (which can change after
the call), `CultureInfo`, `Stream`, `Type` or a delegate, is build error
[MCG018](../user-guide/DIAGNOSTICS.md#mcg018), also as a property of your own record. Pass a value that identifies
it, such as `culture.Name`, or wrap it in a type that implements `ICacheKeyProvider`. Other libraries' models, such
as an SDK's classes in a `Microsoft.*` namespace, are written by their public properties like your own types.

## Readable keys while debugging

`JsonKeyGenerator` writes the arguments it handles as JSON (for example `[42,"alice"]`) instead of a hash:

<!-- snippet: keys-json-generator -->
```cs
builder.Services.AddMethodCache();
builder.Services.Replace(ServiceDescriptor.Singleton<ICacheKeyGenerator, JsonKeyGenerator>());
```
<!-- endSnippet -->

The JSON is followed by a short hash of the arguments' runtime types (`[42,"alice"]:9c41…`), so arguments that
write the same JSON, such as `1` and `1L` or two classes with the same properties, still get different keys. A value
inside an argument whose type differs from its declared type is written with its type, for example
`[{"\uFFFFt":"System.Int64","v":1}]` for `new object[] { 1L }`.

Both generators are in `MethodCache.Extensibility`. Keys built by the decorator itself (simple values, records,
`ICacheKeyProvider`) don't change with the generator.

## Using an argument as the key

`[CacheKey(UseAsRawKey = true)]` on a parameter makes that argument the whole key. The interface, the method name, the
`Version` and the other arguments are not part of it:

<!-- snippet: keys-raw -->
```cs
public interface IDocumentStore
{
    // The argument is the whole key: GetAsync("doc:2026/report.pdf") is stored as "doc:2026/report.pdf"
    [Cache] Task<byte[]> GetAsync([CacheKey(UseAsRawKey = true)] string key);
}
```
<!-- endSnippet -->

Strings are used as they are. Dates are written as in generated keys (`MethodCacheKeys.Part`), in the round-trip
format (`2026-01-01T12:00:00.2500000Z`): fractions of a second and a `DateTimeOffset`'s offset are part of the key,
and a local `DateTime` is written as UTC, so the key doesn't depend on the server's time zone and a local time and the
UTC time of the same instant share an entry. Numbers and other formattable values are written in the invariant culture;
any other type uses its own `ToString()`, so make sure that doesn't depend on the current culture. The key is not escaped and has no
prefix, so two methods, or a `GetOrCreateAsync` call, that produce the same text share one entry. Use it only for keys
your own code builds, never for text a caller controls; see [Security](../security.md).

## Building a key yourself

`MethodCacheKeys` builds the key the decorator uses, so you can invalidate or inspect one entry without guessing the
format:

<!-- snippet: invalidation-by-key -->
```cs
var key = MethodCacheKeys.For(typeof(IOrderService), nameof(IOrderService.GetAsync), MethodCacheKeys.Part(42));
await cache.InvalidateByKeysAsync(key);   // "Docs.Invalidation.IOrderService.GetAsync:42"
```
<!-- endSnippet -->

For a method with a `Version`, use `MethodCacheKeys.ForVersion`. For an overloaded method, pass its
[method id](configuration.md#appsettingsjson) as the name, such as `FindAsync(string)`:

<!-- snippet: keys-versioned-and-overloaded -->
```cs
// A method with a Version: "MyApp.IInvoiceService.GetAsync:v2:42"
var versioned = MethodCacheKeys.ForVersion(
    typeof(IInvoiceService), nameof(IInvoiceService.GetAsync), 2, MethodCacheKeys.Part(42));

// An overloaded method: pass its method id. "MyApp.IInvoiceService.FindAsync(string):INV-7"
var overload = MethodCacheKeys.For(
    typeof(IInvoiceService), "FindAsync(string)", MethodCacheKeys.Part("INV-7"));
```
<!-- endSnippet -->

`MethodCacheKeys.Prefix(type, name, version)` returns the start that every key of one method shares, for example for
`ICacheAdministrator.ListEntries(keyPrefix: ...)`. `MethodCacheKeys.Pattern` returns the same followed by `*`.

## Things to know

- **Keys contain the arguments.** Don't cache methods that take secrets such as passwords or tokens as arguments; pass
  an identifier instead. Hashed keys are not encryption. No argument, whatever characters it contains, can produce
  another call's key; see [Security](../security.md).
- **`Version` changes every key** of a method, so values cached by an earlier version are ignored. See
  [Expiration](expiration.md#versions).
- **Overloaded methods** have their parameter types in their keys, so overloads never share entries. See
  [method ids](configuration.md#appsettingsjson).
- **A method name that another interface in the hierarchy also declares** gets the same treatment. Keys start with
  the decorated interface, so for `ICatalog : ILookup` with `ILookup.Find(int)` and `ICatalog.Find(string)` the keys
  are `MyApp.ICatalog.Find(int):7` and `MyApp.ICatalog.Find(string):7`. When two base interfaces declare the same
  signature, the declaring interface is added too: for `IC : IA, IB` that both declare `Get(int)`, the keys are
  `MyApp.IC.MyApp.IA.Get(int):1` and `MyApp.IC.MyApp.IB.Get(int):1`. Policy ids don't change; each
  method keeps the id of the interface that declares it.
- To see the keys your application produces, list them with `ICacheAdministrator.ListEntries()` or the admin
  endpoints ([Monitoring](monitoring.md)).

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.
