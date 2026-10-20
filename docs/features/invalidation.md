[← MethodCache documentation](../README.md)

# Invalidation

Expiration removes entries eventually; invalidation removes them the moment you know the data changed.

## Tags

Give cached methods tags, and list the same tags on the methods that change the data:

<!-- snippet: invalidation-tags -->
```cs
public interface IOrderService
{
    [Cache(Duration = "00:10:00", Tags = ["orders", "order:{orderId}"])]
    Task<Order> GetAsync(int orderId);

    [Cache(Duration = "00:05:00", Tags = ["orders", "customer:{customerId}:orders", "customer:{customerId}"])]
    Task<List<Order>> ListForCustomerAsync(int customerId);

    // Removes this order and the customer's list, nothing else
    [CacheInvalidate(Tags = ["order:{orderId}", "customer:{customerId}:orders", "customer:{customerId}"])]
    Task CancelAsync(int orderId, int customerId);

    // Removes every cached order
    [CacheInvalidate(Tags = ["orders"])]
    Task ImportAsync(Stream file);
}
```
<!-- endSnippet -->

- `[CacheInvalidate]` removes every entry with any of its tags once the method has completed successfully. If it
  throws, nothing is removed.
- A tag can be shared by many methods, so one write clears every cached view of the same data.
- `[CacheInvalidate]` without tags removes nothing and is reported as [MC0007](../user-guide/DIAGNOSTICS.md#mc0007).

## Tag templates

`"order:{orderId}"` is filled in from the method's arguments: `GetAsync(42)` is tagged `order:42`, and
`CancelAsync(42, 7)` removes `order:42`. The name in braces must be a parameter of the method it is on. The exact
spelling is used first; otherwise a single case-insensitive match is used. A name that matches several parameters
only by case, such as `{NAME}` for `name` and `Name`, stays literal text and is reported as
[MC0017](../user-guide/DIAGNOSTICS.md#mc0017). An unknown name is reported as
[MC0010](../user-guide/DIAGNOSTICS.md#mc0010), with a code fix that offers the closest parameter. Arguments use canonical key-part formatting: strings escape backslashes and colons, GUIDs use the 32-digit `N`
format, and null uses `\0`. Braces in an argument remain literal: an id of `"{org}"` gives
the tag `order:{org}` on both the cached call and the invalidation.

A template takes a parameter name only. `"{order.Id}"` is not a template and stays literal text, so every call would
share one tag; it is reported as [MC0016](../user-guide/DIAGNOSTICS.md#mc0016). Add the id as a parameter, as `UpdateAsync(int id, Product product)` does in
[Getting started](../getting-started.md).

Static tags never format method arguments. A template formats only the parameters it references, in the invariant culture; an unrelated argument's `ToString()` is not invoked.

Tags set in startup code, `appsettings.json` or runtime overrides can use templates too, with any parameter of the
method. They are added to the `[Cache]` attribute's tags, so the attribute's `[CacheInvalidate]` tags keep working;
only an explicit `ClearTags()` (or `"ClearTags": true` in `appsettings.json`) replaces them
([Configuration](configuration.md)). An override with `o => o.AddTags("incident")` on `GetAsync` above gives its
entries the tags `orders`, `order:{orderId}` and `incident`.

`[CacheInvalidate]` also works on methods with `ref` or `ref readonly` returns, and on `Task` and `ValueTask` methods
with `ref`, `out` or `in` parameters. The tags are filled in after the call returns, and the entries are removed only
once the task has completed successfully.

## From code

`ICacheManager` invalidates by tag or by key:

<!-- snippet: invalidation-from-code -->
```cs
await cache.InvalidateTagsAsync(["order:42", "customer:7:orders"]);
```
<!-- endSnippet -->

<!-- snippet: invalidation-by-key -->
```cs
var key = keys.For<IOrderService>(orders => orders.GetAsync(42));
await cache.InvalidateKeyAsync(key);   // "[MyApp]Docs.Invalidation.IOrderService.GetAsync:42"
```
<!-- endSnippet -->

<!-- snippet: invalidation-pattern -->
```cs
await cache.InvalidateTagAsync("customer:7");   // add this tag to every customer entry
```
<!-- endSnippet -->

- `IMethodCacheKeys.For` returns the key the generated decorator uses for that call, including overloads, versions,
  generic methods and keyed registrations; resolve `IMethodCacheKeys` from dependency injection. See
  [Building a key yourself](keys.md#building-a-key-yourself).
- Invalidation matches exact tags and keys; there are no wildcard patterns. Attach a common tag to all entries in a
  group instead.
- To remove every entry of one method, pass its id to `ICacheAdministrator.InvalidateMethodAsync`; get the id from
  `IMethodCacheKeys.MethodId` ([Cache keys](keys.md#building-a-key-yourself)).
- The admin endpoints and `ICacheAdministrator` also invalidate by tag and by method ([Monitoring](monitoring.md)).

## With several instances

With [Redis or SQL Server](distributed-cache.md), invalidating a tag or key removes it from the distributed cache and
tells every instance to drop its in-memory copy. You don't need to do anything else.

