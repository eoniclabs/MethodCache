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
| [MCG003](#mcg003) | Error | `[Cache]` with `ref`, `out` or `in` parameters | Remove `[Cache]` |
| [MCG004](#mcg004) | Error | `[Cache]` and `[CacheInvalidate]` on the same method | |
| [MCG005](#mcg005) | Warning | `[CacheInvalidate]` without tags | |
| [MCG006](#mcg006) | Error | `[Cache]` with a pointer parameter | Remove `[Cache]` |
| [MCG007](#mcg007) | Error | `[Cache]` with a `Span<T>` or other ref struct parameter | Remove `[Cache]` |
| [MCG009](#mcg009) | Warning | A tag names a parameter the method does not have | Use the closest parameter |
| [MCG010](#mcg010) | Error | `[assembly: CacheInterface]` names a type it can't decorate | |
| [MCG011](#mcg011) | Error | `[assembly: CacheInterface]` lists a method it can't cache | |
| [MCG012](#mcg012) | Error | The same interface in more than one `[assembly: CacheInterface]` | |
| [MCG013](#mcg013) | Error | An interface with static abstract members | |
| [MCG014](#mcg014) | Error | `[Cache]` on a lazy sequence | Remove `[Cache]` |

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
[Third-party Caching Guide](THIRD_PARTY_CACHING.md).

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

`[Cache]` on a method with `ref`, `out` or `in` parameters. Those can't be part of a cache key, and a cached call
could not set an `out` value. Pass the values by value and return them, for example as a record.

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

`[Cache]` on a method with a pointer parameter. Pointers can't be part of a cache key; pass a value or an array.

## MCG007

`[Cache]` on a method with a `Span<T>`, `ReadOnlySpan<T>` or other ref struct parameter. They can't be stored in a
cache key; pass an array, `Memory<T>` or a string.

## MCG009

A tag such as `"user:{userId}"` names a parameter the method does not have, so the braces stay literal text and every
call shares that tag. The name between the braces must match a parameter name (case doesn't matter).

**Code fix:** offers the closest parameter names, so `"user:{usrId}"` becomes `"user:{userId}"`.

## MCG010

`[assembly: CacheInterface]` names a type MethodCache can't decorate: a class, a generic interface, an interface that
is not accessible, one with static abstract members, or one declared in your own project (put `[Cache]` on its methods
instead). It is also reported when the attribute lists no methods. See the [Third-party Caching Guide](THIRD_PARTY_CACHING.md).

## MCG011

`[assembly: CacheInterface]` lists a method that is missing, overloaded, or returns nothing to cache. Use
`nameof(IClient.Method)` so a rename is caught by the compiler. See the
[Third-party Caching Guide](THIRD_PARTY_CACHING.md).

## MCG012

The same interface is listed in more than one `[assembly: CacheInterface]`. List each interface once, with all its
methods in the same attribute.

## MCG013

The interface has static abstract members, so no class can implement it as a decorator. Move the cached methods to an
interface without them.

## MCG014

`[Cache]` on a method returning `IEnumerable<T>`, `IAsyncEnumerable<T>` or `IQueryable<T>`. The cached value would be
the lazy sequence itself, which runs the query again on every enumeration. Return a materialized collection, such as
`Task<List<T>>`, `Task<IReadOnlyList<T>>` or `T[]`, and call `ToListAsync()` (or similar) in the implementation.
