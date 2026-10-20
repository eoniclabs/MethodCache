# Build diagnostics

MethodCache checks your attributes when you build. Errors mean the method would not be cached as written, so the
build stops; warnings point at attributes that do nothing. Every diagnostic links to its section below, and the ones
marked **Code fix** can be fixed from the light bulb in Visual Studio, Rider or VS Code.

| Id | Severity | Summary | Code fix |
|----|----------|---------|----------|
| [MC0001](#mc0001) | Warning | `[Cache]` or `[CacheInvalidate]` on a class member | Move to the interface |
| [MC0004](#mc0004) | Error | A time span that is not a positive `TimeSpan` | |
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
| [MC0023](#mc0023) | Error | `RefreshAhead` not shorter than `Duration` on the same attribute | |

## MC0001

`[Cache]` or `[CacheInvalidate]` is on a class, struct or record member. MethodCache generates a decorator for the
interface, so it only reads attributes on interface methods, and this one is ignored.

```csharp
public interface IUserService
{
    [Cache(Duration = "00:05:00")]      // here
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
`[assembly: CacheInterface]` is not a positive time span. Use the `TimeSpan` format: `"00:05:00"` (five minutes),
`"01:00:00"` (an hour) or `"1.00:00:00"` (a day). `SlidingExpiration`, `RefreshAhead` and `FailSafe` also accept `"off"`.

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

Return a concrete type, such as `Task<Product>` or `Task<List<Product>>`, to share the results through the distributed
cache. When only the in-memory cache is used, or memory-only is what you want, suppress the warning with
`#pragma warning disable MC0020` or `<NoWarn>` in the project.

## MC0021

An explicitly supplied `Version` is negative. Omit it to inherit the configured version or use a non-negative integer.

## MC0023

`RefreshAhead` on `[Cache]` or `[assembly: CacheInterface]` is as long as the `Duration` next to it, or longer. The
refresh window starts that long before an entry expires, so every hit would reload the value. Use a window shorter than
the `Duration`, such as `Duration = "00:10:00", RefreshAhead = "00:01:00"`, or `"off"` to turn refresh-ahead off.

## Diagnostic ID migration

The pre-1.0 diagnostic IDs have been consolidated. Update `.editorconfig` severities and suppressions using this mapping; old IDs are intentionally not emitted.

| Previous ID | Current ID |
|-------------|------------|
| MC0001 | MC0001 |
| MC0004 (duration) | MC0004 (unchanged) |
| MCG001 | MC0003 |
| MCG002 | MC0022 |
| MCG003 | MC0005 |
| MCG004 | MC0006 |
| MCG005 | MC0007 |
| MCG006 | MC0008 |
| MCG007 | MC0009 |
| MCG009 | MC0010 |
| MCG010 | MC0011 |
| MCG011 | MC0012 |
| MCG012 | MC0013 |
| MCG013 | MC0014 |
| MCG014 | MC0015 |
| MCG015 | MC0016 |
| MCG016 | MC0017 |
| MCG017 | MC0018 |
| MCG018 | MC0019 |
| MCG019 | MC0020 |

When generator and analyzer run together, the generator owns interface/assembly
attribute validation; the analyzer recognizes its assembly-local generated marker
and reports those settings only when used independently. Class-attribute feedback
remains in the analyzer. MC0004 keeps its duration meaning; MC0022 replaces MCG002.
