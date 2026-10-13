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

    [Cache(Duration = "00:05:00", Tags = ["orders", "customer:{customerId}:orders"])]
    Task<List<Order>> ListForCustomerAsync(int customerId);

    // Removes this order and the customer's list, nothing else
    [CacheInvalidate(Tags = ["order:{orderId}", "customer:{customerId}:orders"])]
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
- `[CacheInvalidate]` without tags removes nothing and is reported as [MCG005](../user-guide/DIAGNOSTICS.md#mcg005).

## Tag templates

`"order:{orderId}"` is filled in from the method's arguments: `GetAsync(42)` is tagged `order:42`, and
`CancelAsync(42, 7)` removes `order:42`. The name in braces must be a parameter of the method it is on. The exact
spelling is used first; otherwise a single case-insensitive match is used. A name that matches several parameters
only by case, such as `{NAME}` for `name` and `Name`, stays literal text and is reported as
[MCG016](../user-guide/DIAGNOSTICS.md#mcg016). An unknown name is reported as
[MCG009](../user-guide/DIAGNOSTICS.md#mcg009), with a code fix that offers the closest parameter. An argument's text is inserted as it is, even if it contains braces: an id of `"{org}"` gives
the tag `order:{org}` on both the cached call and the invalidation.

A template takes a parameter name only. `"{order.Id}"` is not a template and stays literal text, so every call would
share one tag; it is reported as [MCG015](../user-guide/DIAGNOSTICS.md#mcg015). Add the id as a parameter, as `UpdateAsync(int id, Product product)` does in
[Getting started](../getting-started.md).

Tags set in startup code, `appsettings.json` or runtime overrides can use templates too, with any parameter of the
method.

`[CacheInvalidate]` also works on methods with `ref` or `ref readonly` returns, and on `Task` and `ValueTask` methods
with `ref`, `out` or `in` parameters. The tags are filled in after the call returns, and the entries are removed only
once the task has completed successfully.

## From code

`ICacheManager` invalidates by tag, by key or by pattern:

<!-- snippet: invalidation-from-code -->
```cs
await cache.InvalidateByTagsAsync("order:42", "customer:7:orders");
```
<!-- endSnippet -->

<!-- snippet: invalidation-by-key -->
```cs
var key = MethodCacheKeys.For(typeof(IOrderService), nameof(IOrderService.GetAsync), MethodCacheKeys.Part(42));
await cache.InvalidateByKeysAsync(key);   // "Docs.Invalidation.IOrderService.GetAsync:42"
```
<!-- endSnippet -->

<!-- snippet: invalidation-pattern -->
```cs
await cache.InvalidateByTagPatternAsync("customer:7:*");   // in-memory cache only
```
<!-- endSnippet -->

- `MethodCacheKeys.For` builds the key the generated decorator uses. Versioned and overloaded methods need a little
  more; see [Building a key yourself](keys.md#building-a-key-yourself).
- Patterns match tags, with `*` for any number of characters and `?` for one. With a distributed cache configured,
  `InvalidateByTagPatternAsync` throws `NotSupportedException`, because the distributed cache can't list its tags;
  invalidate exact tags instead.
- The admin endpoints and `ICacheAdministrator` also invalidate by tag ([Monitoring](monitoring.md)).

## With several instances

With [Redis or SQL Server](distributed-cache.md), invalidating a tag or key removes it from the distributed cache and
tells every instance to drop its in-memory copy. You don't need to do anything else.

