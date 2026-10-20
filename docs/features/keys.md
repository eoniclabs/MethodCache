[← MethodCache documentation](../README.md)

# Cache keys

You never write keys for `[Cache]` methods. The generated decorator builds each key from the interface, the method
and the arguments, so two calls share an entry exactly when they call the same method with equal arguments.

Chapter 10 of the [console demo](../../samples/MethodCache.Demo/) includes the tenant ID in both the key
and the invalidation tag, keeping the same product ID separate for two tenants. Derive that tenant from an
authorized identity in an application; cache-key separation does not authorize access.

<!-- snippet: keys-arguments -->
```cs
public sealed record UserQuery(string Name, int Page);

public interface IUserService
{
    [Cache] Task<User> GetAsync(int id);                     // [MyApp]MyApp.IUserService.GetAsync:42
    [Cache] Task<List<User>> SearchAsync(UserQuery query);   // [MyApp]MyApp.IUserService.SearchAsync:(alice:2)
    [Cache] Task<List<User>> ByIdsAsync(int[] ids);          // [MyApp]MyApp.IUserService.ByIdsAsync:[3:1:2:3]
    [Cache] Task<int> CountAsync(Dictionary<string, int[]> filters); // [MyApp]MyApp.IUserService.CountAsync:{hash} (hashed)
}
```
<!-- endSnippet -->

Examples below assume the service assembly is named `MyApp`. Its simple name prefixes every key as `[MyApp]`;
assembly versions are excluded, so redeploying a new version preserves key identity. Closed generic argument types
also include their assembly identity. Assemblies with the same namespace/type names therefore keep separate entries.
Assembly-name characters outside letters, digits, `.`, `_` and `-` are escaped as `%XXXX` UTF-16 code units.
Use `IMethodCacheKeys` to get the exact key of a call ([Building a key yourself](#building-a-key-yourself)); the admin method list shows the method ids configuration uses.

## How arguments become keys

- **Simple values** (strings, numbers, `bool`, `Guid`, dates, enums) are written as they are: `GetAsync:42`.
- **Records, classes and structs** whose public properties and fields are simple values are written field by field:
  `SearchAsync:(alice:2)`. Two records with equal values give the same key.
- **Types that implement `ICacheKeyProvider`** contribute their `CacheKeyPart`.
- **Arrays and collections** of those values are written with their count and then each element in order:
  `ByIdsAsync:[3:1:2:3]`. Records and classes inside a collection must be sealed, since an unsealed element type
  could hold a subclass with more members. A dictionary counts when its keys and values are simple values. When a
  collection's runtime type differs from its declared type (an array or a list passed as `IEnumerable<int>`), the key
  includes that type, so an array and a list with the same elements stay two entries. Unknown collection subclasses passed through a base class or interface use the key generator so their own runtime state is preserved.
- **Collection subclasses with public state**, such as a list with a tenant or scope property, use the key generator. Their keys include both that public state and the elements, including when the collection is behind a base-class property. This applies to the internal fallback generator.
- **Everything else**, such as an interface or `object` argument or a collection of collections, goes through the key
  generator. The internal default generator hashes the arguments into a key such as `CountAsync:fc08ab0d…`.
- **Framework types** are written by their value wherever they appear, not by their public properties; see
  [Framework types](#framework-types). Other framework types are a build error.
- **`nint` and `nuint`** also go through the key generator, as the 64-bit number they hold, so a value gets the same
  key in 32-bit and 64-bit processes. Their type is kept: `(nint)1`, `1` and `1L` are three keys, also inside an
  `object[]` or an `object` property.
- **Values inside hashed arguments keep their runtime type.** Where a value's type differs from the type it is declared
  as (an element of `object[]` or `List<object>`, an `object` property, a property typed as a base class or interface),
  the key records the value's version-independent assembly/type identity and all of its members: `new object[] { 1 }` and `new object[] { 1L }` get
  different keys, and so do two `Circle`s behind a `Shape` property that differ only in `Radius`.
- A `CancellationToken` parameter is never part of the key.
- Keys don't depend on the current culture: `-1`, `1.5` and dates are written the same under every culture.
- `0.0` and `-0.0` are two keys in every key form (`1 / x` tells them apart). `NaN` and the infinities work anywhere,
  also inside a hashed argument, such as `double.PositiveInfinity` as a "no limit" value in a filter record.

On .NET 10 the decorator writes simple keys into a stack buffer and looks them up without allocating.

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
[MC0019](../user-guide/DIAGNOSTICS.md#mc0019), also as a property of your own record. Pass a value that identifies
it, such as `culture.Name`, or wrap it in a type that implements `ICacheKeyProvider`. Other libraries' models, such
as an SDK's classes in a `Microsoft.*` namespace, are written by their public properties like your own types.

## Key construction and value serialization

Key construction belongs to the runtime. Complex arguments use the internal canonical encoder; custom key parts use
`ICacheKeyProvider`. JSON and MessagePack settings select distributed **value** serialization and do not replace the
key generator.

<!-- snippet: keys-json-generator -->
```cs
builder.Services.AddMethodCache(cache => cache.UseJson());
```
<!-- endSnippet -->

## Method scope

Generated keys always include the assembly simple name, interface, method, version, closed generic types, the implementation type when an interface has several registrations, and the service key of a keyed registration. An argument cannot replace that scope: `CacheKeyAttribute` and its `UseAsRawKey` flag have been removed. Use `ICacheKeyProvider` to customize one argument's part, or the manual `ICacheManager` API when supplying a complete key.

<!-- snippet: keys-scoped -->
```cs
public interface IDocumentStore
{
    // The document path is an argument; the interface and method remain part of the key.
    [Cache] Task<byte[]> GetAsync(string key);
}
```
<!-- endSnippet -->

## Building a key yourself

To invalidate or inspect one entry, ask `IMethodCacheKeys` for the key of the call. Resolve it from dependency
injection; the compiler checks the call, so overloads, versions, generic methods and argument formatting can't be
gotten wrong:

<!-- snippet: invalidation-by-key -->
```cs
var key = keys.For<IOrderService>(orders => orders.GetAsync(42));
await cache.InvalidateKeyAsync(key);   // "[MyApp]Docs.Invalidation.IOrderService.GetAsync:42"
```
<!-- endSnippet -->

<!-- snippet: keys-versioned-and-overloaded -->
```cs
// A method with a Version: "[MyApp]MyApp.IInvoiceService.GetAsync:v2:42"
var versioned = keys.For<IInvoiceService>(invoices => invoices.GetAsync(42));

// An overloaded method: "[MyApp]MyApp.IInvoiceService.FindAsync(string):INV-7"
var overload = keys.For<IInvoiceService>(invoices => invoices.FindAsync("INV-7"));
```
<!-- endSnippet -->

`For` runs the call on a stand-in that records the key the decorator would use, under the method's current policy, so
it also follows a runtime version change. The method itself never runs. The call must reach exactly one cached method;
anything else, such as a method without `[Cache]`, throws an `InvalidOperationException`. For a keyed registration,
pass its service key: `keys.For<IRates>(rates => rates.Get("EUR"), serviceKey: "ecb")`.

To work with every entry of one method, ask for its method id instead. It is the id the admin method list shows and
appsettings uses, and it covers all arguments, versions, generic arguments and keyed registrations of the method:

<!-- snippet: keys-method-id -->
```cs
// "[MyApp]MyApp.IInvoiceService.FindAsync(string)": the arguments don't matter
var methodId = keys.MethodId<IInvoiceService>(invoices => invoices.FindAsync(""));
var entries = admin.ListEntries(methodId: methodId);
```
<!-- endSnippet -->

<!-- snippet: keys-invalidate-method -->
```cs
var methodId = keys.MethodId<IInvoiceService>(invoices => invoices.FindAsync(""));
var result = await admin.InvalidateMethodAsync(methodId);   // every result of the method, on every instance
```
<!-- endSnippet -->

`CacheEntryInfo.MethodId` tells which method an entry belongs to; it is `null` for entries stored through
`ICacheManager` directly. An application `ICacheManager` stores entries under `MethodCacheKeys.StoredKey(key, version)`;
see [Manual caching](manual-caching.md).

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
  are `[MyApp]MyApp.ICatalog.Find(int):7` and `[MyApp]MyApp.ICatalog.Find(string):7`. When two base interfaces declare the same
  signature, the declaring interface is added too: for `IC : IA, IB` that both declare `Get(int)`, the keys are
  `[MyApp]MyApp.IC.[MyApp]MyApp.IA.Get(int):1` and `[MyApp]MyApp.IC.[MyApp]MyApp.IB.Get(int):1`. Policy ids don't change; each
  method keeps the id of the interface that declares it.
- **Keyed registrations** have their service key after the method name, so `AddKeyedScoped<IRates, EcbRates>("ecb")`
  and `AddKeyedScoped<IRates, FedRates>("fed")` each cache their own results: `[MyApp]MyApp.IRates.Get@ecb:1` and
  `[MyApp]MyApp.IRates.Get@fed:1`. Other keys carry their type (`Get@[MyApp]MyApp.Region.Eu`, `Get@Int32(7)`), and a
  string with a `.`, `(` or `"` in it is written in quotes, so keys of different types never share entries. The service
  key must be a string, enum, bool, char, integer or `Guid`; `AddMethodCache()` throws for a keyed registration of a
  cached interface with any other key, and for a `KeyedService.AnyKey` registration. A constructor parameter marked
  `[ServiceKey]` receives your key, as it does without MethodCache.
- **Several registrations of one interface** under the same service key (or under none) with different
  implementation types each cache their own results. Their keys carry the implementation type before any service key:
  with `AddSingleton<IRates, EcbRates>()` and `AddSingleton<IRates, FedRates>()` the keys are
  `[MyApp]MyApp.IRates.Get#[MyApp]MyApp.EcbRates:1` and `[MyApp]MyApp.IRates.Get#[MyApp]MyApp.FedRates:1`. A single
  registration keeps the plain key. Two registrations the container builds from the same implementation type, such as
  a library and the application both calling `AddSingleton<IRates, EcbRates>()`, are interchangeable and share their
  results. An instance registration can be configured differently, so `AddMethodCache()` fails when one has the same
  type as another registration under that key; a factory that creates the same type as another registration fails
  when both have been created. Give each its own service key instead. `keys.For<IRates>(...)` returns the key of the
  last registration, the one `GetService<IRates>()` returns.
- **All registrations of a method share its method id and its tags.** A `[CacheInvalidate]` tag, on any registration,
  and `InvalidateMethodAsync` remove the matching entries of all of them.
- **Generic methods** carry their closed type arguments after the method name, before any implementation type or
  service key: `Get<[System.Private.CoreLib]System.Int32>:1`. Overloads that differ only in generic arity stay apart in
  ids and keys: `Get(int)`, ``Get`1(int)`` and ``Get`2(int)``. `keys.For` writes the type arguments for you, and the
  method id covers every closed type.
- To see the keys your application produces, list them with `ICacheAdministrator.ListEntries()` or the admin
  endpoints ([Monitoring](monitoring.md)).
