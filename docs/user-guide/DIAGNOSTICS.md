# Build diagnostics

MethodCache checks your attributes when you build. An error stops the build: the attribute asks for something
MethodCache can't do, such as caching a `void` method, or a setting is invalid. A warning lets the build go on: the
attribute is ignored, such as `[Cache]` on a class member or on a `static` interface member, or it probably doesn't do
what you meant, such as a tag placeholder that matches no parameter. Every diagnostic links to its section below, and
the ones marked **Code fix** can be fixed from the light bulb in Visual Studio, Rider or VS Code. When both the
generator and the analyzer run, each problem is reported once. Information, such as MC0037, shows in the editor and
never stops a build.

| Id | Severity | Summary | Code fix |
|----|----------|---------|----------|
| [MC0001](#mc0001) | Warning | `[Cache]` or `[CacheInvalidate]` on a class member | Move to the interface |
| [MC0004](#mc0004) | Error | A duration that is not a positive number and a unit, such as `"10m"` | Write a `TimeSpan` with a unit |
| [MC0003](#mc0003) | Error | `[Cache]` on a `void` method | Remove `[Cache]` |
| [MC0022](#mc0022) | Error | `[Cache]` on `Task`/`ValueTask` without a result | Remove `[Cache]` |
| [MC0005](#mc0005) | Error | `[Cache]` with reference parameters or returns | Remove `[Cache]` |
| [MC0006](#mc0006) | Error | `[Cache]` and `[CacheInvalidate]` on the same method | |
| [MC0007](#mc0007) | Warning | `[CacheInvalidate]` without tags | |
| [MC0008](#mc0008) | Error | `[Cache]` with pointer or function pointer parameters or returns, including arrays of them | Remove `[Cache]` |
| [MC0009](#mc0009) | Error | `[Cache]` with ref struct or potentially ref-like parameters or returns | Remove `[Cache]` |
| [MC0010](#mc0010) | Warning | A tag names a parameter the method does not have | Use the closest parameter |
| [MC0011](#mc0011) | Error | `[assembly: CacheInterface]` names a type it can't decorate | |
| [MC0012](#mc0012) | Error | `[assembly: CacheInterface]` lists a method it can't cache | |
| [MC0013](#mc0013) | Error | The same interface in more than one `[assembly: CacheInterface]` | |
| [MC0014](#mc0014) | Error | An interface with static or non-public abstract members | |
| [MC0015](#mc0015) | Error | `[Cache]` on a lazy sequence | Remove `[Cache]` |
| [MC0016](#mc0016) | Warning | A tag placeholder that is not a parameter name, such as `{product.Id}` | |
| [MC0017](#mc0017) | Warning | A tag placeholder matches several parameter names without an exact spelling match | |
| [MC0018](#mc0018) | Warning | `[Cache]` or `[CacheInvalidate]` on a `static`, non-public or `sealed` interface member | |
| [MC0019](#mc0019) | Error | A key argument, element or member has a framework type without a key form, such as `StringBuilder` | |
| [MC0020](#mc0020) | Warning | A cached result type of `object`, an interface or an abstract class, which a distributed cache can't restore | |
| [MC0021](#mc0021) | Error | An explicitly negative cache version | |
| [MC0023](#mc0023) | Error | `RefreshAhead` not shorter than `Duration` or `SlidingExpiration` on the same attribute | |
| [MC0024](#mc0024) | Warning | A value in a hand-built tag is written differently from MethodCache's tags | Wrap the tag in `MethodCacheKeys.Tag`, or format the value with `MethodCacheKeys.Part` |
| [MC0025](#mc0025) | Warning | A cached result type that isn't safe to share between callers, such as `JsonObject`, `DataTable` or `Stream` | |
| [MC0026](#mc0026) | Warning | The implementation of a cached interface takes per-request state, such as `IHttpContextAccessor` or `ClaimsPrincipal`, or a class that does | |
| [MC0027](#mc0027) | Warning | `ExecuteUpdate`, `ExecuteDelete` or raw SQL writes past EF Core's change tracker without an invalidation | |
| [MC0028](#mc0028) | Warning | A tag on a cached method that nothing in the project invalidates | |
| [MC0029](#mc0029) | Warning | A tag in `InvalidatedElsewhere` or `InvalidatedManually` that matches none of the method's `Tags` | |
| [MC0030](#mc0030) | Warning | A program with cached services that never calls `AddMethodCache` | |
| [MC0031](#mc0031) | Warning | A class calls its own cached method, which skips the cache | |
| [MC0032](#mc0032) | Warning | A class that implements a cached interface is created with `new`, so its calls aren't cached | |
| [MC0033](#mc0033) | Error | `CacheWhen<T>`, `DurationFor<T>`, `SlidingFor<T>` or `TagsFor<T>` for a type the call's result can never be | |
| [MC0034](#mc0034) | Error | A key argument, or a value inside one, of a type whose state is all private | |
| [MC0035](#mc0035) | Warning | A cache factory uses a cancellation token from outside it that the call doesn't pass | Use the factory's token and pass yours as `cancellationToken` |
| [MC0036](#mc0036) | Warning | A name in `methodcache.request_state_types` matches no type | |
| [MC0037](#mc0037) | Info | A cache key that is written when the call runs, because an argument's actual type decides it | |
| [MC0038](#mc0038) | Warning | `SlidingExpiration` not shorter than `Duration` on the same attribute, so reads never extend an entry | |
| [MC0039](#mc0039) | Warning | A class that derives from a type a cached result holds, but that MethodCache can't name to the distributed cache, such as a generic or private one | |
| [MC0040](#mc0040) | Warning | A derived type the `JsonSerializerContext` passed to `UseJson` has no contract for, while it has one for the base type | Add `[JsonSerializable]` to the context |
| [MC0041](#mc0041) | Warning | A cached result holds EF Core entities with virtual navigation properties in a project that uses lazy-loading proxies | |
| [MC0042](#mc0042) | Warning | A cached result type, or a type in it, whose state is all non-public, so serializers write it as an empty object | |
| [MC0043](#mc0043) | Warning | Code changes a cached result | |

## Rules of a later version

A new version of MethodCache can bring new analyzer rules, and with warnings treated as errors a new warning stops the
build. To upgrade first and turn the new rules on later, set `MethodCacheAnalysisLevel` to the version whose rules you
want, in the project file or in `Directory.Build.props`:

```xml
<PropertyGroup>
  <MethodCacheAnalysisLevel>1.0</MethodCacheAnalysisLevel>
</PropertyGroup>
```

The rules that came after that version are then off. Turn one of them on in `.editorconfig`, such as
`dotnet_diagnostic.MC0060.severity = warning`, and raise the level when you have looked at the rest. Every rule of
1.0 stays on at any level, and without the property every rule is on. A value that isn't a version, such as `latest`,
fails the build with MCB001.

Every rule up to 1.0.0 counts as a rule of 1.0, so `1.0` turns nothing off in the release candidates. Set the level
only when warnings are errors. Without that, leave it unset, so an upgrade shows its new warnings.

## MC0001

`[Cache]` or `[CacheInvalidate]` is on a class, struct or record member. MethodCache generates a decorator for the
interface, so it only reads attributes on interface methods, and this one is ignored.

```csharp
public interface IUserService
{
    [Cache(Duration = "5m")]      // here
    Task<User> GetAsync(int id);
}

public class UserService : IUserService
{
    public Task<User> GetAsync(int id) => ...;   // not here
}
```

**Code fix:** moves the attribute to the interface method the member implements (adding the `using` if needed), or
removes it when the member implements no interface. To cache an interface you don't own, see the
[third-party caching page](../features/third-party.md).

## MC0004

`Duration`, `SlidingExpiration`, `RefreshAhead`, `FailSafe`, `FailSafeTimeout` or `FailSafeRetryAfter` on `[Cache]` or
`[assembly: CacheInterface]` is not a duration. Write a positive number and a unit: `"30s"`, `"5m"`, `"1.5h"` or `"7d"`
([Writing a duration](../features/expiration.md#writing-a-duration)). `SlidingExpiration`, `RefreshAhead` and
`FailSafe` also accept `"off"`.

A `TimeSpan` such as `"00:10:00"` is reported with the same value in units, `"10m"`.

**Code fix:** writes a `TimeSpan` with a unit.

## MC0003

`[Cache]` on a `void` method: there is no result to cache. Return the value, or remove `[Cache]`. If the method
changes data, you probably want `[CacheInvalidate(Tags = ...)]` instead.

## MC0022

`[Cache]` on a method returning `Task` or `ValueTask` without a result. Return `Task<T>` or `ValueTask<T>`, or remove
`[Cache]`. A write method usually wants `[CacheInvalidate(Tags = ...)]` instead.

## MC0005

`[Cache]` on a method with `ref`, `out` or `in` parameters, or a `ref` / `ref readonly` return.
A cached value cannot preserve references or set an `out` value. Pass and return values by value, for example as a record.

## MC0006

`[Cache]` and `[CacheInvalidate]` on the same method. A method either reads (cached) or writes (invalidates); keep
the one that matches what it does.

## MC0007

`[CacheInvalidate]` without tags removes nothing. List the tags of the cached methods it should clear:

```csharp
[Cache(Tags = new[] { "users", "user:{id}" })]
Task<User> GetAsync(int id);

[CacheInvalidate(Tags = new[] { "users", "user:{id}" })]
Task UpdateAsync(int id, User user);
```

## MC0008

`[Cache]` on a method with pointer or function pointer parameters or returns, including arrays of them such as
`int*[]`. These cannot be stored as cached values or keys; use a value or an array of values. Unsupported methods, and
any other member of the interface that uses a pointer, are still forwarded through an unsafe implementation.

## MC0009

`[Cache]` on a method with `Span<T>`, `ReadOnlySpan<T>` or other ref struct parameters or returns. These cannot be
stored in a cache; use an array, `Memory<T>` or a string. This also applies to a type parameter with `allows ref struct` when that parameter is used as an argument or return value.

## MC0010

A tag such as `"user:{userId}"` names a parameter the method does not have, so the braces stay literal text and every
call shares that tag. The name between the braces must match a parameter name: the exact spelling first, otherwise a
single case-insensitive match. A name that matches several parameters only by case is [MC0017](#mc0017).

A template takes a parameter name only; a property path such as `"{product.Id}"` is reported as [MC0016](#mc0016).

**Code fix:** offers the closest parameter names, so `"user:{usrId}"` becomes `"user:{userId}"`.

## MC0011

`[assembly: CacheInterface]` names a type MethodCache can't decorate: a class, a generic interface, an interface that
is not accessible, one with static abstract or non-public abstract members, or one declared in your own project (put `[Cache]` on its methods
instead). It is also reported when the attribute lists no methods. See the [third-party caching page](../features/third-party.md).

## MC0012

`[assembly: CacheInterface]` lists a method that is missing, overloaded or static, or one that `[Cache]` would reject:
it returns nothing to cache, has `ref`, `out`, `in`, pointer or ref struct parameters, a framework type without a key
form ([MC0019](#mc0019)), or returns a lazy sequence. Use
`nameof(IClient.Method)` so a rename is caught by the compiler. See the
[third-party caching page](../features/third-party.md).

## MC0013

The same interface is listed in more than one `[assembly: CacheInterface]`. List each interface once, with all its
methods in the same attribute.

## MC0014

The interface has static abstract members, or non-public abstract members that cannot be forwarded to the decorated
instance. Move the cached methods to an interface without them. Non-public default methods are supported because they
remain on the interface, and so are nested helper types declared inside the interface.

## MC0015

`[Cache]` on a method returning `IEnumerable<T>`, `IAsyncEnumerable<T>` or `IQueryable<T>`. The cached value would be
the lazy sequence itself, which runs the query again on every enumeration. Return a materialized collection, such as
`Task<List<T>>`, `Task<IReadOnlyList<T>>` or `T[]`, and call `ToListAsync()` (or similar) in the implementation.

## MC0016

A tag has braces that look like a template but don't hold a plain parameter name: a property path such as
`"product:{product.Id}"`, spaces such as `"{ id }"`, or a format such as `"{day:yyyy-MM-dd}"`. Templates take a
parameter name only, so the braces stay literal text and every call shares the same tag. Write `"product:{id}"` and
add the value as its own parameter, as `UpdateAsync(int id, Product product)` does in
[Getting started](../getting-started.md). For `[assembly: CacheInterface]` both MC0010 and MC0016 are reported on the
attribute.

## MC0017

The placeholder has no exact parameter match and more than one case-insensitive match, such as `{NAME}` for
parameters `name` and `Name`. Use the intended parameter's exact spelling. Until corrected, the placeholder remains
literal in both cached tags and invalidation.

## MC0018

`[Cache]` or `[CacheInvalidate]` is on an interface member the decorator can't intercept: a `static` member, a
`private` or other non-public member with a body, or a `sealed` one. The decorator implements the interface for a
class, and a class can't implement those members, so calls to them never reach the cache and the attribute does
nothing. Put the attribute on a public interface method without `static`, `private` or `sealed` (one without a body,
or a public default method), or remove it.

## MC0019

A key argument has a type from .NET itself, ASP.NET Core or Microsoft.Extensions (an assembly named `System.*`,
`Microsoft.AspNetCore.*` or `Microsoft.Extensions.*`) that the cache key can't be written from: the
argument itself, an element of a collection or tuple, or a public property or field of your own type, such as
`StringBuilder`, `CultureInfo`, `Stream`, `Type` or a `Func<T>`. Their public properties don't hold their value, or the
value can change after the call (a `StringBuilder`), so two different arguments could share a key and one caller
would get another's result. The message names the value, such as `'note.Text' is a 'StringBuilder'`.

Pass a value that identifies it instead, such as a string, an id or a culture name, or wrap it in a type that
implements `ICacheKeyProvider` (see [Cache keys](../features/keys.md#framework-types)). Every argument must have a supported key form.

Framework types with a key form are written by their value: `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`,
`DateOnly`, `TimeOnly`, `Half`, `Int128`, `UInt128`, `BigInteger`, `Complex`, `Uri`, `Version`, `IPAddress` and
`JsonElement` (as its JSON, so whitespace doesn't count), `Index`, `Range`, `Vector2`, `Vector3`, `Vector4`,
`Quaternion`, `Plane`, `Matrix3x2`, `Matrix4x4`, and ASP.NET Core's `PathString`, `QueryString` and `HostString`, as
well as arrays, collections, `Memory<T>`, tuples and `KeyValuePair` of those. Models from other libraries, such as
an SDK's classes in a `Microsoft.*` namespace, are not framework types: they are written by their public properties
like your own.

## MC0020

The cached method's result type, after `Task<T>` or `ValueTask<T>`, is `object`, an interface such as `IComparable` or
`IShape`, or an abstract class, or has one as an element (`List<object>`). A distributed cache stores results as
JSON or MessagePack of the declared type, which can't say which class to create when it reads them back. When a
distributed cache is used, the default round-trip check finds that such a result doesn't read back, and keeps it in the
in-memory cache only. Collection interfaces such as
`IReadOnlyList<Product>` are fine; they are read back as a list.

Not reported when the project, or a project it references, has classes that implement the
interface or derive from the abstract class: MethodCache's generator then names them to its serializers, so they come
back as themselves ([Derived types](../features/distributed-cache.md#derived-types)).

Return a concrete type, such as `Task<Product>` or `Task<List<Product>>`, to share the results through the distributed
cache, or name the types that implement it with `[JsonDerivedType(typeof(Square), "square")]` on the interface or
abstract class (`[Union]` with MessagePack), which also silences this warning. When only the in-memory cache is used, or memory-only is what you want, suppress the warning with
`#pragma warning disable MC0020` or `<NoWarn>` in the project.

## MC0021

An explicitly supplied `Version` is negative. Omit it to inherit the configured version or use a non-negative integer.

## MC0023

`RefreshAhead` on `[Cache]` or `[assembly: CacheInterface]` is as long as the `Duration` or `SlidingExpiration` next to
it, or longer. The refresh window starts that long before an entry expires, so every hit would reload the value. Use a
window shorter than both, such as `Duration = "10m", RefreshAhead = "1m"`, or `"off"` to turn refresh-ahead
off.

## MC0024

A tag built with string interpolation for `InvalidateTagAsync`, `InvalidateTagsAsync` or `InvalidateAfterCommit` contains a value that
interpolation writes differently from the tags MethodCache stores, so the invalidation can match nothing and the old
value stays cached, without an error:

```cs
// The cached entry is tagged price:ollama:gemma3\:4b, so this removes nothing
await cache.InvalidateTagAsync($"price:{provider}:{model}");
```

Tag templates write values in one canonical way: `:` and `\` escaped in strings, a `Guid` as 32 digits without
hyphens, an enum as its number, `true` and `false` in lower case, and numbers and dates in the invariant culture
(dates in ISO 8601). Whole numbers come out the same, so `$"user:{id}"` with an `int` is not reported.

Build the tag from a call to the cached method instead, so the template and the formatting come from the method:

```cs
await cache.InvalidateTagAsync(keys.Tag<IPrices>(s => s.GetRateAsync(provider, model), "price:{provider}:{model}"));
```

Or wrap the interpolated string in `MethodCacheKeys.Tag`, which writes every value the way tags do:
`MethodCacheKeys.Tag($"price:{provider}:{model}")`. A value of a type tags can't hold, or with a format, doesn't
compile there.

**Code fix:** wraps the tag in `MethodCacheKeys.Tag` when every value in it can go there, and otherwise formats the
reported value with `MethodCacheKeys.Part`: `$"price:{MethodCacheKeys.Part(provider)}:{id}"`. A value with a format
such as `{price:F2}` has no fix, since the tag MethodCache stores has no format.

## MC0025

The cached method returns a type whose instances change while they are read or used, or has one as an element:
`JsonNode` and its subclasses (`JsonObject`, `JsonArray`), `DataTable`, `DataSet`, `DataRow`, `DataView`, a `Stream`
or a `StringBuilder`. The cache hands the same instance to every caller, at the same time. A `JsonNode` parses its
content lazily the first time it is read, and isn't thread-safe while it does, so two requests reading one cached
`JsonObject` at once can get an empty or broken result. A stream's position moves as one caller reads it.

```cs
[Cache(Duration = "5m")]
Task<JsonObject> GetLayoutAsync(string locale);   // MC0025: every caller shares one JsonObject
```

Return an immutable type instead: a record, a string (`node.ToJsonString()`), a `byte[]` you never change, or a
`JsonElement` (`JsonDocument.Parse(json).RootElement.Clone()`), which is read-only and safe to share.
Collections such as `List<T>` and arrays are not reported: they are safe to read from several threads, but don't change
a cached one ([cached objects are shared](../features/memory-cache.md#cached-objects-are-shared)).

## MC0026

A class implements an interface with `[Cache]` methods and takes per-request state in its constructor:
`IHttpContextAccessor`, `HttpContext`, `HttpRequest`, `ClaimsPrincipal` or another `IPrincipal`. It is reported too
when it takes a class whose constructor takes that state, such as a `DbContext` that filters rows by the signed-in user,
directly or through a factory such as `IDbContextFactory<T>`. The analyzer follows constructors two levels down (the
class the implementation takes, and a class that class takes), and follows an interface to the classes that implement
it, in the same project and in the projects it references
([what the analyzers read from referenced projects](#what-the-analyzers-read-from-referenced-projects)). It doesn't
see a setting that an interceptor makes on the connection.

Name your own request-state types, such as an `ICurrentUser` service or a tenant context, in `.editorconfig`, comma
separated, with namespace. A class that takes one, or takes a class that does, is reported too:

```ini
[*.cs]
methodcache.request_state_types = MyApp.ICurrentUser, MyApp.TenantContext
```

A name that matches no type, such as one left behind when the type was renamed, is reported as
[MC0036](#mc0036).

The cache key is built from the method's arguments only. A result that depends on the current request or user, such
as a query filtered by row-level security on a connection set up for that user, is stored under a key that doesn't
name the user, and the next caller gets it, whoever they are. Refresh-ahead and fail-safe also load values outside
the request, in a new scope with no `HttpContext`.

```cs
public sealed class Orders(IHttpContextAccessor http, AppDb db) : IOrders   // MC0026
{
    public Task<List<Order>> MineAsync() => db.Orders.Where(o => o.UserId == http.HttpContext!.User.Id()).ToListAsync();
}
```

```cs
public sealed class TenantDb(DbContextOptions<TenantDb> options, IHttpContextAccessor http) : DbContext(options) { ... }

public sealed class Catalog(TenantDb db) : ICatalog   // MC0026: TenantDb's constructor takes IHttpContextAccessor
```

Make what the result depends on a parameter of the cached method, so it becomes part of the key:
`Task<List<Order>> ForUserAsync(int userId)`, called with the current user's id. Or don't cache the method.
For a context set up for the signed-in user, give the cached reads a context of their own
([Row-level security](../features/entity-framework-core.md#row-level-security)).
See [Getting started](../getting-started.md#per-user-data).

### A tenant parameter checked against the request

When the cached method already takes the tenant or user as a parameter, and reads through a context that filters by
the request's tenant, check that the two match. Mark your check method `[ChecksRequestState]` and call it with the
parameter at the start of each cached method. A class whose cached methods all do isn't reported:

```cs
public sealed class TenantDb(DbContextOptions<TenantDb> options, ITenantContext tenant) : DbContext(options)
{
    [ChecksRequestState]
    public void EnsureActsFor(Guid tenantId)
    {
        if (tenantId != tenant.Id) throw new UnauthorizedAccessException("Not the request's tenant.");
    }
}

public sealed class Projects(TenantDb db) : IProjects
{
    public Task<List<ProjectDto>> ListAsync(Guid tenantId)   // [Cache] on IProjects.ListAsync
    {
        db.EnsureActsFor(tenantId);   // a load for another tenant throws, so nothing wrong is cached under its key
        return db.Projects.Select(p => new ProjectDto(p.Id, p.Name)).ToListAsync();
    }
}
```

The check runs when the method loads a value, not on a hit. What is cached under a tenant's key was loaded for that
tenant, but a caller that passes another tenant's id gets that tenant's cached result without the check. So take the
id from the request, or authorize it, before the call, as for any cached method that takes a tenant or user id.
Refresh-ahead runs without a request, so the check fails there and the entry is loaded on the next call instead;
don't combine the pattern with `RefreshAhead`. The attribute changes nothing at run time.

The analyzer reads the attribute on the method the cached method calls. When you call the check through an interface,
such as `ITenantContext.EnsureActsFor(tenantId)`, mark the interface method; the attribute on the class that implements
it isn't seen through the interface.

### A table the request doesn't filter

Some cached reads go through a context that holds request state but read a table that doesn't depend on it, such as
looking up an API key before the tenant is known, from a table without row-level security. The result depends only on
the arguments, but the analyzer can't tell which tables a method reads, so it still reports the class. Suppress the
warning there, with the reason, so it stays next to the code it is about:

```cs
[SuppressMessage("Security", "MC0026", Justification = "api_keys has no row-level security; the key is the argument")]
public sealed class ApiKeys(TenantDb db) : IApiKeys
{
    public Task<ApiKey?> FindAsync(string keyHash)   // [Cache] on IApiKeys.FindAsync
        => db.ApiKeys.SingleOrDefaultAsync(k => k.Hash == keyHash);
}
```

Suppress it on the class, not for the project, so a new cached method in another class is still checked. A context
of its own for such reads, without the request state, avoids the warning as well.

## MC0027

Reported in a project that uses `MethodCache.EntityFrameworkCore`, on `ExecuteUpdate`, `ExecuteDelete`,
`ExecuteSql`, `ExecuteSqlRaw` or `ExecuteSqlInterpolated` (and their `Async` versions). They change rows without
loading them, so EF Core's change tracker never sees the rows, and no entity tags are invalidated for them. Cached
results that read those rows stay until they expire.

- `ExecuteUpdate` and `ExecuteDelete` are reported on the entity types that the project maps with
  `ef.Entity<T>(...)`, and on their base and derived types. A type mapped only with `WhenDeleted(...)` is reported
  for `ExecuteDelete` alone. A project that references the one with the mapping, such as its tests or a worker,
  checks its writes against that mapping too: MethodCache writes the mapping into the assembly as `AssemblyMetadata`
  attributes ([where to see them](#what-the-analyzers-read-from-referenced-projects)). When neither the project nor a project it references maps a type, as in a data layer that the startup
  project references, every type is reported.
- Raw SQL is reported when it names the table of such a type, runs code (`call`, `exec`, `execute` or `do`), or
  can't be read: SQL in a variable, or `ExecuteSqlRaw` with an interpolated value that is neither a constant nor a
  number, such as a string, which could hold any SQL. A table is known by its class name, the name of its `DbSet` and
  a name given with `ToTable` or `[Table]`, compared without case and underscores and with or without a plural ending,
  so `ai_model_prices` matches `AiModelPrice`. A name given with `ToTable` in a project that maps no type isn't
  recognized. When no mapping is visible, all raw SQL is reported.
- Statements that only `select`, such as `select pg_advisory_xact_lock(...)`, or `set` a session setting, such as
  `SET LOCAL lock_timeout = '5s'`, are never reported. That holds for `ExecuteSqlRaw` with interpolated constants and
  numbers too, such as `$"SET LOCAL lock_timeout = '{waitMs}ms'"` with an `int`.

```cs
await db.Products.Where(p => p.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 0m));   // MC0027
```

Invalidate the tags of the rows it changes, in the same method or in a method around it, such as the one that runs an
execution strategy: inside a transaction with `db.InvalidateAfterCommit(...)`, or after a write that is already
committed with `ICacheManager.InvalidateTagsAsync(...)`
([Writes the change tracker doesn't see](../features/entity-framework-core.md#writes-the-change-tracker-doesnt-see)).
A call to a `[CacheInvalidate]` method of a cached interface counts as an invalidation, and so does a call to a method
of your own marked `[InvalidatesCache]`, such as a helper that invalidates through the app's own plumbing. The warning
is not reported inside either kind of method, since they invalidate themselves.
When nothing cached reads the rows of a mapped type, turn it off for that line with `#pragma warning disable MC0027`.

A generic helper, such as a retention job's `PurgeAsync<TEntity>()`, is reported, since any type could reach it.
When some of the types it gets are cached, have it take the tags of what it changes, invalidate them, and mark it
`[InvalidatesCache]`, so each call to it counts as the invalidation
([Generic helpers](../features/entity-framework-core.md#generic-helpers)). When the types it gets are never cached,
turn the warning off in the helper and say why:

```cs
public static class Retention
{
    // Called for audit and log types only, which nothing caches
#pragma warning disable MC0027
    public static Task<int> PurgeAsync<TEntity>(DbSet<TEntity> rows, DateTime before) where TEntity : class, IStamped
        => rows.Where(r => r.CreatedAt < before).ExecuteDeleteAsync();
#pragma warning restore MC0027
}
```

## MC0028

A tag on a `[Cache]` method that nothing in the project invalidates, in a project that invalidates something. Its
entries stay until they expire, so a change to the data they hold isn't seen until then.

```cs
public interface ICatalog
{
    [Cache(Duration = "1h", Tags = ["catalog"])]   // MC0028 when nothing invalidates "catalog"
    Task<IReadOnlyList<Recipe>> ListAsync();
}
```

These invalidations count, wherever they are in the project:

- A `[CacheInvalidate]` tag in the project, or on a method of an interface in a project it references, such as a
  shared invalidation interface that another project calls.
- An entity mapping of `MethodCache.EntityFrameworkCore`: `Entity<T>("...")`, and `Tags("...")` or
  `WhenDeleted("...")` in `Entity<T>(e => ...)`.
- A call to `InvalidateTagAsync`, `InvalidateTagsAsync` or `InvalidateAfterCommit`, or to a method of your own marked
  `[InvalidatesCache]`, with the tag as a constant, an interpolated string or `IMethodCacheKeys.Tag(..., template)`.
- A tag in `InvalidatedElsewhere` on any `[Cache]` in the project, for a tag that another application invalidates.
- A tag in `InvalidatedManually` on any `[Cache]` in the project, for a tag that only an operator clears.

A placeholder matches any value: `"product:{id}"` is covered by `InvalidateTagAsync($"product:{id}")` and by
`"product:42"`, not by `"products"`. A tag built in a variable, tags in configuration and other invalidations in other
projects aren't seen; `TagCoverage` in `MethodCache.Testing` checks the whole application in a test, and checks that
a referenced project invalidates each tag marked `InvalidatedElsewhere`
([Testing](../features/testing.md#every-tag-has-an-invalidation)).

Invalidate the tag where the data changes ([Invalidation](../features/invalidation.md)). When another application or
process invalidates it, such as a worker that writes the data, list it in `InvalidatedElsewhere`
([In another application](../features/invalidation.md#in-another-application)):

```cs
// The import worker invalidates "catalog"
[Cache(Duration = "1h", Tags = ["catalog"], InvalidatedElsewhere = ["catalog"])]
Task<IReadOnlyList<Recipe>> ListAsync();
```

When no code invalidates it and an operator clears it by hand after a change, such as with `POST invalidate` of
`MapMethodCacheAdmin`, list it in `InvalidatedManually` instead
([Cleared by hand](../features/invalidation.md#cleared-by-hand)):

```cs
// An operator clears "catalog" after the yearly update
[Cache(Duration = "1d", Tags = ["catalog"], InvalidatedManually = ["catalog"])]
Task<IReadOnlyList<Recipe>> ListAsync();
```

Both replace a `SuppressMessage` on each method and keep the reason next to the tag.

A project that invalidates nothing, such as a library that only declares the cached interfaces, isn't reported.

## MC0029

A tag in `InvalidatedElsewhere` or `InvalidatedManually` on `[Cache]` that matches none of the method's `Tags`, such
as a misspelled one. It marks nothing on the method, so the real tag still counts as never invalidated.

```cs
[Cache(Duration = "1h", Tags = ["catalog"], InvalidatedElsewhere = ["catalogue"])]   // MC0029
Task<IReadOnlyList<Recipe>> ListAsync();
```

Write the tag as it is written in `Tags`. A placeholder matches any value, so `"recipe:{Id}"` matches
`"recipe:{id}"`. Not reported when the attribute has no `Tags`, because the method's tags then come from
configuration.

## MC0030

A program has cached services, in its own code or in a project it references, but its code calls neither
`AddMethodCache` nor a method of a referenced project that calls it. Nothing of MethodCache runs then, so every
`[Cache]` method runs uncached, and nothing at run time can tell.

```cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<IProducts, Products>();   // IProducts has [Cache] methods
var app = builder.Build();                             // MC0030: no AddMethodCache
```

Call `AddMethodCache()` after registering the services ([Getting started](../getting-started.md)):

```cs
builder.Services.AddScoped<IProducts, Products>();
builder.Services.AddMethodCache();
```

A setup method of your own in a referenced project counts when the program calls it, such as
`builder.Services.AddData()` whose code calls `AddMethodCache`. MethodCache marks such methods when it builds that
project, also when they reach `AddMethodCache` through other setup methods of the project named `Add...`, `Use...`,
`Configure...` or `Register...`. A program that references the project but never calls the method is reported, since
nothing sets the cache up for it. ([What the analyzers read from referenced projects](#what-the-analyzers-read-from-referenced-projects)
shows where these marks are.)

Test projects aren't reported, since tests may use the implementations without the cache. A program that references a
project with cached services but doesn't use them, such as a migration tool, or that reaches its setup some other way,
such as through reflection, can turn the warning off with `<NoWarn>$(NoWarn);MC0030</NoWarn>`.

## MC0031

A class calls one of its own `[Cache]` methods. The cache is a generated decorator in front of the service, so a call
from inside the class goes straight to the method and runs every time, also through `this` or a cast of `this` to
the interface.

```cs
public sealed class Prices(IPriceApi api) : IPrices
{
    public Task<decimal> GetAsync(int id) => api.GetAsync(id);   // [Cache] on IPrices.GetAsync

    public async Task<decimal> TotalAsync(int[] ids)
    {
        var total = 0m;
        foreach (var id in ids) total += await GetAsync(id);   // MC0031: not cached
        return total;
    }
}
```

Move the cached method to a service of its own and call it through that service's interface, so the call passes
the cache:

```cs
public sealed class Totals(IPrices prices) : ITotals
{
    public async Task<decimal> TotalAsync(int[] ids)
    {
        var total = 0m;
        foreach (var id in ids) total += await prices.GetAsync(id);   // cached
        return total;
    }
}
```

When the call doesn't need the cache, such as a method that calls itself to walk a tree, let the cached method call
a private method that does the work, and recurse in that one. The private method isn't cached, and nothing suggests
it is:

```cs
public sealed class Paths(IFolders folders) : IPaths
{
    public Task<string> ResolveAsync(int folderId) => ResolveCoreAsync(folderId);   // [Cache] on IPaths.ResolveAsync

    private async Task<string> ResolveCoreAsync(int folderId)
    {
        var folder = await folders.GetAsync(folderId);
        return folder.ParentId is { } parent ? await ResolveCoreAsync(parent) + "/" + folder.Name : folder.Name;
    }
}
```

A class created with `new` has no decorator either, so its calls aren't cached; [MC0032](#mc0032) reports it.

## MC0032

A class that implements a cached interface is created with `new`. The cache is a decorator that dependency injection
puts in front of the service when `AddMethodCache` runs, so an instance made with `new` has none, and its `[Cache]`
methods run every time.

```cs
var rates = new EcbRates(http);              // MC0032: not cached
var rate = await rates.GetAsync("SEK");
```

Get the interface from dependency injection instead. When you need several instances of one class, such as one per
provider or tenant, register each as a keyed service before `AddMethodCache`; each caches its own results:

```cs
builder.Services.AddKeyedSingleton<IRates>("ecb", (sp, _) => new EcbRates(sp.GetRequiredService<HttpClient>()));
builder.Services.AddKeyedSingleton<IRates>("fed", (sp, _) => new FedRates(sp.GetRequiredService<HttpClient>()));
builder.Services.AddMethodCache();

public sealed class Converter([FromKeyedServices("ecb")] IRates rates) { /* rates is cached */ }
```

Creating the instance inside a registration, as above or as `services.AddSingleton<IRates>(new EcbRates(http))`, isn't
reported, since `AddMethodCache` wraps what the registration gives. Passing it to another constructor first, as in
`new Converter(new EcbRates(http))`, is. For code that can't get its services from dependency injection, cache the call
with [`ICacheManager`](../features/manual-caching.md) instead. Test projects aren't reported, since tests create
implementations to test them without the cache. Where you want the uncached class on purpose, suppress the warning
there with `#pragma warning disable MC0032`.

## MC0033

`CacheWhen<T>`, `DurationFor<T>`, `SlidingFor<T>` or `TagsFor<T>` on `CacheEntryOptions.Builder` is for a `T` that the call's result can
never be, such as `string` for a method that returns `int`. It would get no result to check, so MethodCache rejects it when the
application starts or the call runs; this error finds it while building.

```cs
mc.Method((IPrices p) => p.CountAsync(0)).Configure(o => o.CacheWhen<string>(s => s.Length > 0));   // MC0033
await cache.GetOrCreateAsync<int>("count", LoadCountAsync, o => o.CacheWhen<string>(s => s != null)); // MC0033
```

Use the call's result type, or for one method the typed `Method(...).CacheWhen(...)`, `Method(...).DurationFor(...)`,
`Method(...).SlidingFor(...)` and `Method(...).TagsFor(...)`, which take the result type from the method:

```cs
mc.Method((IPrices p) => p.CountAsync(0)).CacheWhen(count => count > 0);
```

A base type, an interface or the nullable form of the result type is fine: `CacheWhen<object>`,
`CacheWhen<IReadOnlyList<Product>>` for a `List<Product>`, or `CacheWhen<int?>` for an `int`. A numeric type
the result converts to isn't, since a boxed `int` is not a `long`. Reported in `Method(...).Configure(...)`,
`GetOrCreateAsync`, `GetOrCreateManyAsync` and `SetAsync`; options built elsewhere and passed in are checked when the
call runs.

## MC0034

A key argument of a `[Cache]` method, or a value inside it (a property of a record, an element, a tuple item), has a
type of your own whose state is all private: private fields or properties, also inherited ones, and no public property
or field. Every value of it would be written the same, so all calls would share one key and callers would get each
other's results. MethodCache refuses it with an `InvalidOperationException` when the call runs; this error finds it
while building. The message names the value, such as `'query.Owner' is a 'UserId'`.

```cs
public readonly struct UserId(int value) { private readonly int _value = value; }
public interface IUsers { [Cache] Task<User> GetAsync(UserId id); }   // MC0034
```

Implement `ICacheKeyProvider` on the type, or give it public properties that identify the value
([Types whose state is private](../features/keys.md#types-whose-state-is-private)). A type with a `[JsonConverter]`
and an abstract type aren't reported.

## MC0035

The factory of `GetOrCreateAsync` or `GetOrCreateManyAsync` uses a cancellation token from outside it, such as the
method's own `ct` or `HttpContext.RequestAborted`, that the call doesn't pass as its `cancellationToken`. Callers of one
key share one run of the factory, which gets the token of the caller that started it: when that caller cancels, the
others run the factory again. A token the cache doesn't know fails the run for all of them instead, and they get the
cancellation, or a fail-safe value when there is one.

```cs
public ValueTask<Order> GetAsync(int id, CancellationToken ct) =>
    cache.GetOrCreateAsync($"order:{id}", async _ => await orders.GetAsync(id, ct));   // MC0035
```

Use the factory's own token, and pass yours as `cancellationToken`. The code fix does both, and names a discarded
parameter after your token, so inside the factory `ct` is the factory's:

```cs
public ValueTask<Order> GetAsync(int id, CancellationToken ct) =>
    cache.GetOrCreateAsync($"order:{id}", async ct => await orders.GetAsync(id, ct), cancellationToken: ct);
```

A token the call passes as its `cancellationToken`, `CancellationToken.None` and a token the factory makes itself
aren't reported.

## MC0036

A name in the `.editorconfig` option `methodcache.request_state_types` matches no type, although its namespace has types.
[MC0026](#mc0026) then doesn't look for that type, without a sign: this happens when the type is renamed or moved and
the option isn't.

```ini
[*.cs]
methodcache.request_state_types = MyApp.ICurrentUser   # MC0036 after ICurrentUser was renamed IUserContext
```

Write the type's full name as it is now, with its namespace, such as `MyApp.IUserContext`. A nested type is written
`MyApp.Outer.Inner`, and a generic type `MyApp.IRepository<T>`. A name is checked only in a project that sees types in
its namespace, so a solution-wide `.editorconfig` that names a type of the web project doesn't warn in a library that
doesn't reference it, even one whose own namespace, such as `MyApp.Data`, starts with the type's namespace. The warning
has no location in a file; it names the option and the type.

## MC0037

Information, not a warning: the decorator can't write the method's key when you build, so the key generator writes it
when the call runs, from the actual type of the argument, and hashes it. That is the case for an argument of type
`object`, an interface the decorator can't write, or a collection of collections
([How arguments become keys](../features/keys.md#how-arguments-become-keys)). The key is right, but it takes longer to
build and isn't readable in the admin list.

```cs
[Cache(Duration = "5m")]
Task<Page<Order>> SearchAsync(object filter);   // MC0037: the key is written when the call runs
```

To have it written when you build, pass simple values or a sealed record of them:

```cs
public sealed record OrderFilter(string? Customer, OrderStatus? Status, int Page);

[Cache(Duration = "5m")]
Task<Page<Order>> SearchAsync(OrderFilter filter);   // SearchAsync:(alice:Open:2)
```

## MC0038

`SlidingExpiration` on `[Cache]` or `[assembly: CacheInterface]` is as long as the `Duration` next to it, or longer. An
entry never lives past its `Duration`, so reads can never extend it, and it expires as if the sliding window weren't
set:

```cs
[Cache(Duration = "5m", SlidingExpiration = "1d")]   // MC0038: each entry expires 5 minutes after it was loaded
Task<Report> GetAsync(int id);
```

Use a window shorter than the `Duration`, such as `Duration = "1d", SlidingExpiration = "5m"` to keep an entry while it
is read at least every five minutes, for at most a day. Or remove `SlidingExpiration`. It is a warning, not an error:
the method is cached, only the window does nothing.

The two settings often come from different places, such as a sliding window on the method and the duration of
`Defaults`, which the build can't see together. The host logs the same warning when it starts, once for each such
method, whether the settings come from `[Cache]`, startup code or `appsettings.json`
([Sliding expiration](../features/expiration.md#sliding-expiration)).

## MC0039

A class derives from a type that a cached result holds, such as a `Dog` where a method returns `List<Animal>`, but
MethodCache can't name it to the distributed cache's serializer: the class is generic, private or nested in a private
type, `file`-local, or not public in a referenced assembly. A value that holds one can't be restored as a `Dog` from the
distributed cache, so the check before sharing keeps it in each instance's memory, with a warning in the log:

```cs
public class Animal { public string Name { get; set; } = ""; }
public class Box<T> : Animal { public T Value { get; set; } = default!; }   // MC0039: it is generic

public interface IZoo
{
    [Cache] Task<List<Animal>> AnimalsAsync();
}
```

Make the class public or internal, not generic and not nested in a private type, and MethodCache registers it, so it
comes back from the distributed cache as itself. Or name it with `[JsonDerivedType(typeof(Box<int>), "box-int")]` on
the base type (`[Union]` with MessagePack). When such values only need the in-memory cache, suppress the warning. A test
project gets no MC0039, since its private test doubles don't reach a shared cache
([Derived types](../features/distributed-cache.md#derived-types)).

## MC0040

Under Native AOT the distributed cache's JSON can only write the types the `JsonSerializerContext` passed to `UseJson`
has contracts for. The context has one for a type a cached result holds, such as `Animal`, but not for a class that
derives from it, such as `Dog`, so a value holding a `Dog` stays in each instance's memory:

```cs
[JsonSerializable(typeof(List<Animal>))]
internal partial class AppJsonContext : JsonSerializerContext { }

builder.Services.AddMethodCache(mc => mc.UseJson(AppJsonContext.Default));   // MC0040: no contract for Dog
```

Add `[JsonSerializable(typeof(Dog))]` to the context; the code fix does it for you. The derived types that classes in
referenced projects register count too. The host also logs the derived types the context lacks when it starts, for a
context the build can't see, such as one built at run time.

## MC0041

The project references Microsoft.EntityFrameworkCore.Proxies, and a cached result holds a class with public `virtual`
navigation properties, such as an entity that `UseLazyLoadingProxies` loads as a proxy:

```cs
public class Customer
{
    public int Id { get; set; }
    public virtual List<Order> Orders { get; set; } = [];
}

public interface ICustomers
{
    [Cache] Task<List<Customer>> GetAllAsync();   // MC0041
}
```

EF Core returns a proxy class made at run time instead of a `Customer`. The distributed cache can't restore a proxy, so
the check before sharing keeps these results in each instance's memory, and writing one for the cache reads its
navigation properties, which loads them from the database, or throws once the `DbContext` is gone. Return a type
without virtual navigations instead, such as a record the query projects with `Select`:

```cs
public sealed record CustomerSummary(int Id, string Name, int OrderCount);

public Task<List<CustomerSummary>> GetAllAsync()
    => db.Customers.Select(c => new CustomerSummary(c.Id, c.Name, c.Orders.Count)).ToListAsync();
```

## MC0042

A cached result type, an element of it or a type of its public properties, is one of your own types that keeps all its
state in non-public fields or properties. JSON and MessagePack write public properties and fields, so such a value is
written as an empty object. The check before sharing can't tell, since the empty object reads back and writes the same,
so the other instances would get an empty value from the distributed cache:

```cs
public sealed class Money
{
    private readonly decimal _amount;
    public Money(decimal amount) => _amount = amount;
    public decimal Rounded() => Math.Round(_amount, 2);
}

[Cache] Task<Money> GetBalanceAsync(int accountId);   // MC0042: Money is written as {}
```

Give the type public properties, such as `public decimal Amount { get; }` set by the constructor, or a record such as
`public sealed record Money(decimal Amount)`. Or mark the fields `[JsonInclude]`, or give the type a JSON converter
with `[JsonConverter]`. A converter registered only in the options passed to `UseJson` can't be seen by the build;
suppress the warning then.

## MC0043

Code changes a result that a cached method or `ICacheManager.GetOrCreateAsync` returned: it sets a property or field of
the result or of an object in it, adds to, removes from or sorts a collection in it, or writes to an array in it. The
memory cache keeps the instance the method or call returned and hands that same instance to every later caller on this
instance, so they all see the change. The other instances read the copy
in the distributed cache, which doesn't have it, so instances give different answers until the entry expires:

```cs
var orders = await _orders.GetForCustomerAsync(customerId);
orders.Add(draft);                        // MC0043: every later caller on this instance gets the draft too
foreach (var order in orders)
    order.Status = OrderStatus.Shown;     // MC0043
```

Change a copy instead: `new List<Order>(orders)` or `orders.ToList()` for the collection, and a record's `with`
expression, such as `order with { Status = OrderStatus.Shown }`, for an object in it. A list copy still holds the same
objects, so setting a property of one of its elements is reported too. Better still, return types that can't be changed,
such as records with `init` properties and `IReadOnlyList<T>`, so the compiler stops such code. See
[results that don't change](../features/distributed-cache.md#results-that-dont-change).

For an array, such as a `float[]` embedding, copy it before you change it: `(float[])vector.Clone()` or
`vector.ToArray()`. Writing through a `Span<T>` over the cached array, such as `vector.AsSpan()` or
`Span<float> span = vector`, or copying into it with `Array.Copy`, `Buffer.BlockCopy` or `CopyTo`, is reported too. A
`ReadOnlySpan<T>` only reads, so it isn't.

It's reported for the result of a cached method, and of `GetOrCreateAsync`, `GetOrCreateManyAsync` and `TryGetAsync` on
`ICacheManager`, kept in a local or read in a `foreach`. It follows objects reached from it through properties, fields,
indexers, `First`, `Single`, `Last`, `ElementAt` and `Find`, through LINQ copies such as `ToList`, `ToArray` and
`Where`, and through spans over its arrays. Your own methods that change an object, such as `order.Close()`, and a value
you change after passing it to `SetAsync` aren't seen; in Development, MethodCache also compares cached values with what
it stored now and then, and logs a warning naming the method when one has changed. Not reported in test projects.

## What the analyzers read from referenced projects

MC0026, MC0027 and MC0030 also look at the projects yours references, and so does `TagCoverage` in
MethodCache.Testing. MethodCache writes what they need into each project's assembly when it builds that project, as
`[assembly: AssemblyMetadata(...)]` attributes in these generated files:

| Generated file | Key | Written when | Read by |
|----------------|-----|--------------|---------|
| `MethodCacheHostMarkers.g.cs` | `MethodCache.CachedServices` | the project has cached services | [MC0030](#mc0030) |
| `MethodCacheHostMarkers.g.cs` | `MethodCache.AddsMethodCache` | once for each method of the project that calls `AddMethodCache`, directly or through its own setup methods | [MC0030](#mc0030) |
| `MethodCacheEntityMapping.g.cs` | `MethodCache.EntityFrameworkCore.Mapping` | the project maps entities with `ef.Entity<T>(...)` | [MC0027](#mc0027) |
| `MethodCacheInvalidatedTags.g.cs` | `MethodCache.InvalidatedTags` | once for each tag the project invalidates, with `{}` for a placeholder or an interpolated value; not in test projects | `TagCoverage` |
| `MethodCacheRequestState.g.cs` | `MethodCache.RequestState` | once for each interface of a class whose constructor takes per-request state, or takes a class that does, such as `Data.IStats\|RavenStats\|\|IHttpContextAccessor` | [MC0026](#mc0026) |

To see them, open the referenced project's generated files: under Dependencies, .NET, Source Generators,
MethodCache.SourceGenerator in Rider, or Dependencies, Analyzers, MethodCache.SourceGenerator in Visual Studio. When
one of these warnings looks wrong, check that file first. A project built with an older MethodCache, or not rebuilt
since its setup or mapping changed, has old values: rebuild it.
