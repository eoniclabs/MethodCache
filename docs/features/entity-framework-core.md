[← MethodCache documentation](../README.md)

# Entity Framework Core

The `MethodCache.EntityFrameworkCore` package invalidates cached results when Entity Framework Core commits a change.
Map an entity type to the tags of the methods that read it, and every save that adds, changes or deletes one of its
rows invalidates those tags, after the data is committed. Writes don't need invalidation code of their own, also when
they happen inline in an endpoint.

Install it at the same version as `MethodCache`.

## Setting it up

The cached reads are ordinary `[Cache]` methods with tags:

<!-- snippet: efcore-reads -->
```cs
public interface IProductReads
{
    [Cache(Duration = "10m", Tags = ["product:{id}"])]
    Task<ProductDto?> GetAsync(int id);

    [Cache(Duration = "10m", Tags = ["category:{categoryId}"])]
    Task<IReadOnlyList<ProductDto>> ListAsync(int categoryId);

    [Cache(Duration = "1h", Tags = ["top-sellers"])]
    Task<IReadOnlyList<ProductDto>> TopSellersAsync();
}
```
<!-- endSnippet -->

Connect the context in `AddMethodCache`, and say which tags a change to each entity type invalidates:

<!-- snippet: efcore-register -->
```cs
builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(connectionString));
builder.Services.AddScoped<IProductReads, ProductReads>();

builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(ef => ef
    .Entity<Product>("product:{Id}", "category:{CategoryId}")
    .Entity<OrderLine>("top-sellers")));
```
<!-- endSnippet -->

`AddDbContext`, `AddDbContextPool` and `AddDbContextFactory` stay as they are, before or after `AddMethodCache`. A
save is now all a write needs:

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

## Mapping entities to tags

`Entity<TEntity>(tags)` takes the tags to invalidate when a row of that type is added, changed or deleted.

- **A name in braces is a property of the entity**, also a shadow property such as a foreign key that has no property
  in the class. The exact spelling is used first; otherwise the only property that matches without regard to case.
- **Values are written as cached methods write arguments**: a `Guid` as 32 digits without hyphens, an enum as its
  number, `:` and `\` escaped in strings. So `"product:{Id}"` on the entity matches `"product:{id}"` on a method, and
  `"sku:{Sku}"` matches `"sku:{sku}"` for a SKU such as `ham:mer`.
- **A changed row invalidates the tags of its old and its new values.** Moving a product to another category
  invalidates `category:1` and `category:2`.
- **A row you change without loading it is read once before it is saved**, for its stored values. That covers
  `Update(product)` with values from a request, `Attach` with a few properties set, and `Remove(new Product { Id = id })`:
  the category it had in the database is invalidated, not the empty value of a new object. A row the context loaded
  is never read again. With row-level security, see
  [Saves of rows the context didn't load](#saves-of-rows-the-context-didnt-load).
- **A new row is tagged with its saved values**, so a key the database generates is the real one, not a temporary value.
  A cached "not found" for that id is invalidated too.
- **A derived type** invalidates the tags of every mapped type it derives from.
- **A type mapped with `OwnsOne` or `OwnsMany`**, such as an address stored in the customer's row, invalidates the
  tags of its owner. **A many-to-many link** added or removed invalidates the tags of both sides that are mapped.
- **A tag without braces**, such as `"top-sellers"`, is invalidated by any change to that entity type.
- **Everything the save writes counts**: rows another `SaveChanges` interceptor changes, adds or deletes, and rows EF
  Core deletes by cascade during the save.

The tags are checked against the model when the host starts. A name that is not a property, or a type the context
doesn't map, stops the host with a message that lists the entity's properties. When the context can't be created at
startup, because it needs something only a request has, the first save checks them instead.

Map each entity to the tags of every cached method whose result reads it. A method that reads several tables needs
a tag that each of their entities invalidates, such as `top-sellers` above for order lines.

### Rows the database deletes with a row

Rows the database changes by itself aren't seen: rows that `ON DELETE CASCADE` removes when the context hadn't loaded
them, and rows a trigger writes. Give the cached methods that read them a tag that the parent entity invalidates too,
such as `category:{CategoryId}` on products for a method that lists a category with its products.

When the parent is saved often for reasons that change no cached result, such as a customer whose last sign-in is
stored, map those tags with `WhenDeleted`. They are invalidated only when a row of that type is deleted, with the
deleted row's values in the braces:

<!-- snippet: efcore-when-deleted -->
```cs
builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(ef => ef
    .Entity<Product>("product:{Id}", "category:{CategoryId}")
    .Entity<OrderLine>("top-sellers")
    // Every sign-in saves the customer, and changes no cached result. A deleted customer does: the
    // database deletes their order lines, which the context never loaded
    .Entity<Customer>(customer => customer.WhenDeleted("top-sellers"))));
```
<!-- endSnippet -->

`Tags(...)` in the same call maps tags for any change, the same as `Entity<Customer>("...")`, so
`customer => customer.Tags("customer:{Id}").WhenDeleted("top-sellers")` maps both. A type mapped only with
`WhenDeleted` doesn't invalidate anything for an added or changed row. A change to an `OwnsOne` or `OwnsMany` type
or to a many-to-many link doesn't reach those tags either, since it doesn't delete the row they belong to.

## When the tags are invalidated

Never before the data is committed: a reader in between would cache the old rows again.

| Situation | Invalidated |
|---|---|
| `SaveChanges` without a transaction | After it succeeds |
| Inside `BeginTransaction` | After `Commit`. `Rollback` drops them |
| Inside a `TransactionScope` | When the scope ends, also when it aborts: a connection outside the scope may have saved |
| A transaction passed in with `UseTransaction` | When a context connected to MethodCache commits it, or else once it is over: when its connection closes, or when the context is disposed after the commit |
| `SaveChanges` throws | Nothing, when nothing reached the database. When part of it may have, such as a commit that failed on the way back, its tags are invalidated |
| An execution strategy retries | A failed attempt invalidates nothing early; the retry collects its tags again |

<!-- snippet: efcore-transaction -->
```cs
public static async Task MoveAsync(int id, int categoryId, ShopDb db, CancellationToken ct)
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var product = await db.Products.SingleAsync(p => p.Id == id, ct);
    product.CategoryId = categoryId;
    await db.SaveChangesAsync(ct);   // nothing invalidated yet: the transaction is open
    await transaction.CommitAsync(ct);   // product:{id}, the old and the new category:{CategoryId}
}
```
<!-- endSnippet -->

- Synchronous `SaveChanges()` and `Commit()` invalidate the memory cache before they return. Redis, SQL Server or
  PostgreSQL confirm in the background, so the call never waits on the network.
- An invalidation after a commit never throws: the data is saved, and an exception would make the caller think the
  save failed. If the distributed cache is down, the invalidation is retried until it is back, and logged as a warning.
- With several instances, the other instances drop their copies as with any invalidation
  ([With several instances](invalidation.md#with-several-instances)).
- Commit through EF Core: `Commit()` on what `BeginTransaction` returned, or `Database.CommitTransaction()`. A
  transaction that ends some other way, such as a commit on its `DbTransaction`, has its tags invalidated at the
  context's next save or when the context is disposed.
- Where the outcome is unknown, the tags are invalidated: an extra invalidation costs a cache miss, a missing one
  serves old data until it expires.

## Writes the change tracker doesn't see

`ExecuteUpdate`, `ExecuteDelete` and `Database.ExecuteSql…` change rows without loading them, so no entity tags are
known for them. Warning [MC0027](../user-guide/DIAGNOSTICS.md#mc0027) reports such a write when the method around it
doesn't invalidate anything:

- `ExecuteUpdate` and `ExecuteDelete` on a type the project maps with `Entity<T>(...)`, or on a type it derives from
  or that derives from it. A type mapped only with `WhenDeleted` is reported for `ExecuteDelete` alone. Writes to
  tables that nothing caches, such as refresh tokens, aren't reported. In a project that maps no type, such as a
  data layer apart from the startup project, every `ExecuteUpdate` and `ExecuteDelete` is reported.
- `Database.ExecuteSql…` when the SQL names the table of a mapped type, runs code (`call`, `exec`, `do`), or can't be
  read, such as SQL in a variable. A table is known by its class name, `DbSet` name, `ToTable` or `[Table]` name, in
  any case, so `ai_model_prices` matches `AiModelPrice`. Statements that only `select`, such as an advisory lock, or
  `set` a session setting never count. In a project that maps no type, all other raw SQL is reported.
- A generic helper, such as `PurgeAsync<TEntity>()`, since any type could reach it
  ([MC0027](../user-guide/DIAGNOSTICS.md#mc0027) shows what to do).

A call to `InvalidateAfterCommit`, `ICacheManager.InvalidateTagsAsync`, a `[CacheInvalidate]` method or a method of
your own marked `[InvalidatesCache]` counts as the invalidation:

```cs
public static class CacheChanges
{
    // Invalidates through the app's own plumbing: MC0027 counts a call to it
    [InvalidatesCache]
    public static void Changed(this HttpContext http, string tag) => ...;
}
```

A method marked `[InvalidatesCache]` isn't reported for the writes inside it either.

Inside a transaction, queue the tags with `InvalidateAfterCommit`. They are invalidated with the entity tags after the
commit, and dropped on a rollback:

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

Without a transaction, the write is committed when it returns. Invalidate at once:

<!-- snippet: efcore-committed-write -->
```cs
public static async Task DeleteCategoryAsync(ShopDb db, ICacheManager cache, IMethodCacheKeys keys, int categoryId,
    CancellationToken ct)
{
    // Without a transaction the delete is committed when it returns: invalidate at once
    await db.Products.Where(p => p.CategoryId == categoryId).ExecuteDeleteAsync(ct);
    await cache.InvalidateTagAsync(
        keys.Tag<IProductReads>(r => r.ListAsync(categoryId), "category:{categoryId}"), CancellationToken.None);
}
```
<!-- endSnippet -->

`InvalidateAfterCommit` takes tags exactly as cached methods write them. Build them with `IMethodCacheKeys.Tag`, or
with `MethodCacheKeys.Part` around each value; [MC0024](../user-guide/DIAGNOSTICS.md#mc0024) reports a hand-built tag
that formats a value differently. Outside a transaction, queued tags wait for the next commit, a successful
`SaveChanges` or a transaction's `Commit`, and are invalidated when the context is disposed if none comes. A rollback
doesn't drop them, since they don't belong to it.

Writes outside EF Core, such as Dapper or another service, are covered in
[When the write happens somewhere else](invalidation.md#when-the-write-happens-somewhere-else).

## Row-level security

When the database filters rows by a setting on the connection, such as the signed-in user's id, cached reads and
saves of rows the context didn't load work as follows.

### Cached reads

A cached result is shared by every caller with the same arguments, and a refresh-ahead or fail-safe load runs after
the request, without its user. So a cached read can't use a context that is set up for the signed-in user. Give the
cached reads a context of their own, and pass what the policies filter on as an argument:

<!-- snippet: efcore-shared-read-context -->
```cs
// For cached reads: a context of its own, which nothing sets up from the request
public sealed class ReadDb(DbContextOptions<ReadDb> options) : DbContext(options)
{
    public DbSet<Recipe> Recipes => Set<Recipe>();
}

public interface IRecipeReads
{
    [Cache(Duration = "10m", Tags = ["recipes:{householdId}"])]
    Task<IReadOnlyList<RecipeDto>> ListAsync(Guid householdId);
}

public sealed class RecipeReads(IDbContextFactory<ReadDb> factory) : IRecipeReads
{
    public async Task<IReadOnlyList<RecipeDto>> ListAsync(Guid householdId)
    {
        // Its own context and connection for each call, also for a refresh that runs after the request
        await using var db = await factory.CreateDbContextAsync();
        // The policies read the household from the argument, which is part of the key, not from the request
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlAsync($"select set_config('app.household', {householdId.ToString()}, false)");
        return await db.Recipes.Where(r => r.HouseholdId == householdId)
            .Select(r => new RecipeDto(r.Id, r.Title)).ToListAsync();
    }
}
```
<!-- endSnippet -->

Register it next to the app's context:

<!-- snippet: efcore-shared-read-register -->
```cs
// Requests read and write through the app's context, set up for the signed-in user
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(connectionString).AddInterceptors(signedIn));
// Cached reads use their own
builder.Services.AddDbContextFactory<ReadDb>(o => o.UseNpgsql(connectionString));
builder.Services.AddScoped<IRecipeReads, RecipeReads>();

// The writes' context is the one connected to MethodCache
builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<AppDb>(ef => ef
    .Entity<Recipe>("recipes:{HouseholdId}")));
```
<!-- endSnippet -->

- The household is part of the key: each household has its own entry, and a refresh reads the same rows.
- The endpoint checks that the signed-in user belongs to the household before it calls `ListAsync`, as it checks any
  id it takes from a request. For the cached call, the policy no longer does.
- For data every user may read, such as a shared catalog, the read context can connect as a database user that the
  policies let read all of it, and the method needs no argument for it.
- Warning [MC0026](../user-guide/DIAGNOSTICS.md#mc0026) reports a cached implementation that takes the request's
  state, or takes a class whose constructor does, such as a context that takes `IHttpContextAccessor`. It doesn't see
  a setting that an interceptor makes on the connection.

### Saves of rows the context didn't load

A row changed or deleted without loading it is read for its stored values before the save
([Mapping entities to tags](#mapping-entities-to-tags)). The read runs on the save's connection:

- **EF Core query filters don't apply to it**, so a row that a tenant filter hides from queries is still read.
- **Row-level security does.** When a policy hides the row from the user, the read finds nothing, and the values your
  code set are used for the old tags. PostgreSQL and SQL Server hide that row from EF Core's `UPDATE` and `DELETE`
  too, also when a PostgreSQL policy lets the user change rows: the save changes nothing and throws
  `DbUpdateConcurrencyException`. So a save never changes a row whose old tags weren't read.
- On a database that lets a user change a row they can't read, load the row before you change it, or queue the old
  tags yourself with `InvalidateAfterCommit`.

## Document databases

Azure Cosmos DB and MongoDB have EF Core providers. Entity tags and `InvalidateAfterCommit` work with them, since
both act on `SaveChanges`. Cosmos DB has no transactions you begin yourself in EF Core, so each `SaveChanges` is the
commit. A MongoDB transaction you begin with `BeginTransaction` isn't seen by the package; its tags are invalidated
at the context's next save after the commit, or when the context is disposed.

With these providers, a context can't tell which rows it loaded until its first save. A changed row it loaded before
then is read once when it is saved, as a row changed without loading is, if its tags use more than its key.

## Testing

Use SQLite in memory, or your real database, with the same registration. A test that writes to the database without
EF Core needs its own invalidation ([Testing](testing.md#when-a-test-writes-to-the-database-directly)). With
`MethodCache.Testing`, `recorder.Loads` proves a read is cached and `recorder.InvalidatedTags` lists the tags a save
invalidated ([Counting loads and invalidations](testing.md#counting-loads-and-invalidations)).

`EntityTagMapping<TContext>` lists what the mapping holds. `TagCoverage` counts the mapping's tags as invalidated, so a
test can check that every tag of a cached method is mapped or has another invalidation:

<!-- snippet: efcore-tag-coverage -->
```cs
var mapping = provider.GetRequiredService<EntityTagMapping<ShopDb>>();
// [(Product, ["product:{Id}", "category:{CategoryId}"], []), (OrderLine, ["top-sellers"], [])]
var entities = mapping.Entities;

// TagCoverage counts the mapping, so every tag of a cached method is mapped or has another invalidation
Assert.Empty(TagCoverage.Uninvalidated(provider));
```
<!-- endSnippet -->
