[← MethodCache documentation](../README.md)

# Invalidation

Expiration removes entries eventually; invalidation removes them the moment you know the data changed.

## Tags

Give cached methods tags, and list the same tags on the methods that change the data:

<!-- snippet: invalidation-tags -->
```cs
public interface IOrderService
{
    [Cache(Duration = "10m", Tags = ["orders", "order:{orderId}"])]
    Task<Order> GetAsync(int orderId);

    [Cache(Duration = "5m", Tags = ["orders", "customer:{customerId}:orders", "customer:{customerId}"])]
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

## When the write happens somewhere else

`[CacheInvalidate]` covers writes that go through the cached interface. With Entity Framework Core,
[`MethodCache.EntityFrameworkCore`](entity-framework-core.md) invalidates after every commit that changes a mapped
entity, wherever the write happens. When the data changes somewhere else, such as
an endpoint or a command that writes to the database directly, or a transaction that commits after the
`[CacheInvalidate]` method has returned, invalidate after the write or the commit. Get the tag from
`IMethodCacheKeys.Tag`, so you never build it by hand:

<!-- snippet: invalidation-elsewhere-interface -->
```cs
public interface IPriceSource
{
    const string RateTag = "price:{provider}:{model}";

    [Cache(Duration = "5m", Tags = [RateTag])]
    Task<decimal?> GetRateAsync(string provider, string model);
}
```
<!-- endSnippet -->

<!-- snippet: invalidation-elsewhere -->
```cs
public static async Task UpdateRateAsync(string provider, string model, decimal rate,
    PriceDb db, ICacheManager cache, IMethodCacheKeys keys)
{
    await db.SaveRateAsync(provider, model, rate);   // written directly, not through IPriceSource

    // The tag GetRateAsync(provider, model) stored its result under, formatted exactly as the decorator does
    await cache.InvalidateTagAsync(
        keys.Tag<IPriceSource>(s => s.GetRateAsync(provider, model), IPriceSource.RateTag));
}
```
<!-- endSnippet -->

- `keys.Tag<T>(call, template)` fills in the template from the call's arguments exactly as the decorator does. The
  method never runs. The template must be one of the method's tags, or it throws and lists them, so a template that
  drifts away from the method fails in your tests instead of silently matching nothing. A `const` on the interface
  lets the attribute and the call share it.
- For one entry, `cache.InvalidateKeyAsync(keys.For<IPriceSource>(s => s.GetRateAsync(provider, model)))` removes
  only that call's result.
- Don't build the tag with string interpolation. Tags write arguments in one canonical way (`:` and `\` escaped in
  strings, a `Guid` without hyphens, an enum as its number), so `$"price:{provider}:{model}"` can match nothing for a
  model such as `gemma3:4b`, and the old value stays until it expires. The analyzer reports such a tag as
  [MC0024](../user-guide/DIAGNOSTICS.md#mc0024).
- A `[CacheInvalidate]` method with an empty body, such as `Task PriceChangedAsync(string provider, string model)`,
  works too: call it after the write.

### After a database transaction

With Entity Framework Core, [`MethodCache.EntityFrameworkCore`](entity-framework-core.md) does this for you. With
another way to write, such as Dapper or ADO.NET, invalidate after the transaction commits, not before and not inside it. An invalidation before the commit lets a
reader cache the old data again in between, and a rollback after it has nothing to undo. Pass
`CancellationToken.None`: once the data has changed, the invalidation must happen even if the caller has gone.

<!-- snippet: invalidation-after-commit -->
```cs
public static async Task RenameAsync(int id, string name, DbConnection connection, ICacheManager cache,
    IMethodCacheKeys keys, CancellationToken ct)
{
    await using (var transaction = await connection.BeginTransactionAsync(ct))
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "update products set name = @name where id = @id";
        AddParameter(command, "@name", name);
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    // After the commit, and not cancelled with the request: the data has changed either way
    await cache.InvalidateTagAsync(
        keys.Tag<IProductReads>(r => r.GetNameAsync(id), IProductReads.ProductTag), CancellationToken.None);
}
```
<!-- endSnippet -->

To make a forgotten invalidation unlikely, give every write path one place to go. Put the writes behind an interface
with `[CacheInvalidate]` methods that commit their own transaction: the attribute runs after the method returns, so
after the commit, and only when it succeeded. Where writes must stay inline, wrap them in a helper that commits and
then invalidates, and test each write path with a cached read before and after it
([Testing](testing.md#when-a-test-writes-to-the-database-directly)).

## With several instances

With [Redis, SQL Server or PostgreSQL](distributed-cache.md), invalidating a tag or key removes it from the distributed
cache and sends every other instance a message to drop its in-memory copy. You don't need to do anything else, but the
other instances drop their copies when the message arrives, not when the call returns: until then they can serve the
copy they hold. How each provider catches up after a lost connection is in
[What other instances see](distributed-cache.md#what-other-instances-see).

The `InvalidationResult` that `InvalidateTagAsync` and `InvalidateKeyAsync` return counts how far the invalidation
got, per tag or key:

| Count | Means |
|---|---|
| `Local` | Removed from this instance's memory |
| `Distributed` | Confirmed by the distributed cache: instances without a copy in memory, and instances that start later, read the new value |
| `Queued` | Not confirmed by the distributed cache yet. It is retried every `CircuitBreakerDuration`, and until then other instances can serve the old value |
| `Published` | The message to the other instances was sent. Nothing confirms that each one received it |
| `PublicationQueued` | Confirmed by the distributed cache, but the message wasn't sent yet; it is retried |

Without a distributed cache only `Local` counts.

