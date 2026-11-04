# MethodCache.EntityFrameworkCore

Invalidates MethodCache tags when Entity Framework Core commits a change. Map an entity type to the tags of the cached
methods that read it, and every save that adds, changes or deletes one of its rows invalidates them, after the data
is committed, never before.

Install it at the same version as `MethodCache`.

## Why this package

- **No invalidation code in your writes.** An endpoint that loads an entity, changes it and calls `SaveChangesAsync`
  invalidates the right tags by itself.
- **Never too early.** Inside a transaction the tags wait for the commit, so no reader caches the old rows in between,
  and a rollback drops them. `TransactionScope` works too.
- **Old and new values.** Moving a row to another category invalidates both categories, also when you update or
  delete the row without loading it first.
- **The same tags as your methods.** `"product:{Id}"` on the entity matches `"product:{id}"` on a `[Cache]` method,
  formatted the same way, and a name that is not a property stops the host at startup.
- **Rows the database deletes for you.** `Entity<User>(user => user.WhenDeleted("posts"))` invalidates tags only when
  a user is deleted, for rows that `ON DELETE CASCADE` removes with it, while a user saved on every sign-in clears
  nothing.
- **A warning where it can't help.** `ExecuteUpdate` and `ExecuteDelete` on a mapped type and raw SQL go past the
  change tracker; analyzer MC0027 reports them when the method doesn't invalidate. Mark your own invalidation helpers
  `[InvalidatesCache]` and a call to them counts.

## Usage

<!-- snippet: efcore-register -->
```cs
builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(connectionString));
builder.Services.AddScoped<IProductReads, ProductReads>();

builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(ef => ef
    .Entity<Product>("product:{Id}", "category:{CategoryId}")
    .Entity<OrderLine>("top-sellers")));
```
<!-- endSnippet -->

<!-- snippet: efcore-save -->
```cs
public static async Task ChangePriceAsync(int id, decimal price, ShopDb db, CancellationToken ct)
{
    var product = await db.Products.SingleAsync(p => p.Id == id, ct);
    product.Price = price;
    // Invalidates product:{id} and category:{CategoryId} once the save succeeded
    await db.SaveChangesAsync(ct);
}
```
<!-- endSnippet -->

For a write the change tracker doesn't see, queue the tags yourself:

<!-- snippet: efcore-invalidate-after-commit -->
```cs
public static async Task RecalculateAsync(ShopDb db, IMethodCacheKeys keys, int categoryId, CancellationToken ct)
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await db.Products
        .Where(p => p.CategoryId == categoryId)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, p => p.Price * 1.1m), ct);
    // ExecuteUpdateAsync goes past the change tracker: name the tags it changes
    db.InvalidateAfterCommit(keys.Tag<IProductReads>(r => r.ListAsync(categoryId), "category:{categoryId}"));
    await transaction.CommitAsync(ct);
}
```
<!-- endSnippet -->

Works with SQL Server, PostgreSQL, SQLite and the other relational providers, and on `SaveChanges` also with Cosmos DB and
MongoDB. The full guide, with when exactly each tag is invalidated, is
[Entity Framework Core](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/entity-framework-core.md).
