# Build Diagnostics

MethodCache checks your attributes when you build. Errors mean the method would not be cached as written, so the
build stops; warnings point at attributes that do nothing.

| Id | Severity | What it means | Fix |
|----|----------|---------------|-----|
| MC0001 | Warning | `[Cache]` or `[CacheInvalidate]` is on a class, struct or record member. Only interface methods are cached. | Move the attribute to the interface method. |
| MC0004 | Error | `Duration`, `SlidingExpiration` or `RefreshAhead` is not a positive time span. | Use a value such as `"00:05:00"` or `"1.00:00:00"`. |
| MCG001 | Error | `[Cache]` on a `void` method: there is no result to cache. | Return a value, or remove `[Cache]`. |
| MCG002 | Error | `[Cache]` on a method returning `Task` or `ValueTask` without a result. | Return `Task<T>`/`ValueTask<T>`, or remove `[Cache]`. |
| MCG003 | Error | `[Cache]` on a method with `ref`, `out` or `in` parameters. | Pass the values by value. |
| MCG004 | Error | `[Cache]` and `[CacheInvalidate]` on the same method. | Keep one of them. |
| MCG005 | Warning | `[CacheInvalidate]` without tags removes nothing. | Add `Tags = new[] { "..." }`. |
| MCG006 | Error | `[Cache]` on a method with a pointer parameter. | Pass a value or an array instead. |
| MCG007 | Error | `[Cache]` on a method with a `Span<T>` or other ref struct parameter. | Pass an array, `Memory<T>` or a string. |
| MCG009 | Warning | A tag such as `"user:{userId}"` names a parameter the method does not have. | Fix the name between the braces. |
| MCG010 | Error | `[assembly: CacheInterface]` names a type that is not an interface MethodCache can decorate. | See the [Third-party Caching Guide](THIRD_PARTY_CACHING.md). |
| MCG011 | Error | `[assembly: CacheInterface]` lists a method that is missing, overloaded or returns nothing to cache. | See the [Third-party Caching Guide](THIRD_PARTY_CACHING.md). |
| MCG012 | Error | The same interface is listed in more than one `[assembly: CacheInterface]`. | List each interface once, with all its methods. |
| MCG013 | Error | The interface has static abstract members, so it can't be decorated. | Move the cached methods to an interface without them. |
| MCG014 | Error | `[Cache]` on a method returning `IEnumerable<T>`, `IAsyncEnumerable<T>` or `IQueryable<T>`. The cached value would be the lazy sequence, which runs again on every enumeration. | Return a materialized collection, such as `Task<List<T>>` or `IReadOnlyList<T>`. |
