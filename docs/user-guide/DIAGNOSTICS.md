# Build Diagnostics

MethodCache checks your attributes when you build. Errors mean the method would not be cached as written, so the
build stops; warnings point at attributes that do nothing. Every diagnostic links to its section below, and the ones
marked **Code fix** can be fixed from the light bulb in Visual Studio, Rider or VS Code.

| Id | Severity | Summary | Code fix |
|----|----------|---------|----------|
| [MC0001](#mc0001) | Warning | `[Cache]` or `[CacheInvalidate]` on a class member | Move to the interface |
| [MC0004](#mc0004) | Error | A time span that is not a positive `TimeSpan` | |
| [MCG001](#mcg001) | Error | `[Cache]` on a `void` method | Remove `[Cache]` |
| [MCG002](#mcg002) | Error | `[Cache]` on `Task`/`ValueTask` without a result | Remove `[Cache]` |
| [MCG003](#mcg003) | Error | `[Cache]` with reference parameters or returns | Remove `[Cache]` |
| [MCG004](#mcg004) | Error | `[Cache]` and `[CacheInvalidate]` on the same method | |
| [MCG005](#mcg005) | Warning | `[CacheInvalidate]` without tags | |
| [MCG006](#mcg006) | Error | `[Cache]` with pointer or function pointer parameters or returns, including arrays of them | Remove `[Cache]` |
| [MCG007](#mcg007) | Error | `[Cache]` with ref struct parameters or returns | Remove `[Cache]` |
| [MCG009](#mcg009) | Warning | A tag names a parameter the method does not have | Use the closest parameter |
| [MCG010](#mcg010) | Error | `[assembly: CacheInterface]` names a type it can't decorate | |
| [MCG011](#mcg011) | Error | `[assembly: CacheInterface]` lists a method it can't cache | |
| [MCG012](#mcg012) | Error | The same interface in more than one `[assembly: CacheInterface]` | |
| [MCG013](#mcg013) | Error | An interface with static or non-public abstract members | |
| [MCG014](#mcg014) | Error | `[Cache]` on a lazy sequence | Remove `[Cache]` |
| [MCG015](#mcg015) | Warning | A tag placeholder that is not a parameter name, such as `{product.Id}` | |
| [MCG016](#mcg016) | Warning | A tag placeholder matches several parameter names without an exact spelling match | |

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

`Duration`, `SlidingExpiration`, `RefreshAhead`, `FailSafe` or `FailSafeTimeout` on `[Cache]` or
`[assembly: CacheInterface]` is not a positive time span. Use the `TimeSpan` format: `"00:05:00"` (five minutes),
`"01:00:00"` (an hour) or `"1.00:00:00"` (a day). `FailSafe` also accepts `"off"`.

## MCG001

`[Cache]` on a `void` method: there is no result to cache. Return the value, or remove `[Cache]`. If the method
changes data, you probably want `[CacheInvalidate(Tags = ...)]` instead.

## MCG002

`[Cache]` on a method returning `Task` or `ValueTask` without a result. Return `Task<T>` or `ValueTask<T>`, or remove
`[Cache]`. A write method usually wants `[CacheInvalidate(Tags = ...)]` instead.

## MCG003

`[Cache]` on a method with `ref`, `out` or `in` parameters, or a `ref` / `ref readonly` return.
A cached value cannot preserve references or set an `out` value. Pass and return values by value, for example as a record.

## MCG004

`[Cache]` and `[CacheInvalidate]` on the same method. A method either reads (cached) or writes (invalidates); keep
the one that matches what it does.

## MCG005

`[CacheInvalidate]` without tags removes nothing. List the tags of the cached methods it should clear:

```csharp
[Cache(Tags = new[] { "users", "user:{id}" })]
Task<User> GetAsync(int id);

[CacheInvalidate(Tags = new[] { "users", "user:{id}" })]
Task UpdateAsync(int id, User user);
```

## MCG006

`[Cache]` on a method with pointer or function pointer parameters or returns, including arrays of them such as
`int*[]`. These cannot be stored as cached values or keys; use a value or an array of values. Unsupported methods, and
any other member of the interface that uses a pointer, are still forwarded through an unsafe implementation.

## MCG007

`[Cache]` on a method with `Span<T>`, `ReadOnlySpan<T>` or other ref struct parameters or returns. These cannot be
stored in a cache; use an array, `Memory<T>` or a string.

## MCG009

A tag such as `"user:{userId}"` names a parameter the method does not have, so the braces stay literal text and every
call shares that tag. The name between the braces must match a parameter name (case doesn't matter).

A template takes a parameter name only; a property path such as `"{product.Id}"` is reported as [MCG015](#mcg015).

**Code fix:** offers the closest parameter names, so `"user:{usrId}"` becomes `"user:{userId}"`.

## MCG010

`[assembly: CacheInterface]` names a type MethodCache can't decorate: a class, a generic interface, an interface that
is not accessible, one with static abstract members, or one declared in your own project (put `[Cache]` on its methods
instead). It is also reported when the attribute lists no methods. See the [third-party caching page](../features/third-party.md).

## MCG011

`[assembly: CacheInterface]` lists a method that is missing, overloaded, or returns nothing to cache. Use
`nameof(IClient.Method)` so a rename is caught by the compiler. See the
[third-party caching page](../features/third-party.md).

## MCG012

The same interface is listed in more than one `[assembly: CacheInterface]`. List each interface once, with all its
methods in the same attribute.

## MCG013

The interface has static abstract members, or non-public abstract members that cannot be forwarded to the decorated instance. Move the cached methods to an interface without them. Non-public default helpers are supported because they remain on the interface.

## MCG014

`[Cache]` on a method returning `IEnumerable<T>`, `IAsyncEnumerable<T>` or `IQueryable<T>`. The cached value would be
the lazy sequence itself, which runs the query again on every enumeration. Return a materialized collection, such as
`Task<List<T>>`, `Task<IReadOnlyList<T>>` or `T[]`, and call `ToListAsync()` (or similar) in the implementation.

## MCG015

A tag has braces that look like a template but don't hold a plain parameter name: a property path such as
`"product:{product.Id}"`, spaces such as `"{ id }"`, or a format such as `"{day:yyyy-MM-dd}"`. Templates take a
parameter name only, so the braces stay literal text and every call shares the same tag. Write `"product:{id}"` and
add the value as its own parameter, as `UpdateAsync(int id, Product product)` does in
[Getting started](../getting-started.md). For `[assembly: CacheInterface]` both MCG009 and MCG015 are reported on the
attribute.

## MCG016

The placeholder has no exact parameter match and more than one case-insensitive match, such as `{NAME}` for parameters `name` and `Name`. Use the intended parameter’s exact spelling. Until corrected, the placeholder remains literal in both cached tags and invalidation.
