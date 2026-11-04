[← MethodCache documentation](README.md)

# How do I…?

Short answers to common questions, each with a link to the page that has the details. Search this page for a word
from your question.

## Getting started

**Why does MethodCache need an interface?**
The source generator writes a class that implements your interface, checks the cache and calls your implementation
on a miss. Dependency injection hands out that class in place of yours. A class without an interface can't be
replaced that way: add an interface, or use [manual caching](features/manual-caching.md) with `ICacheManager`.
([Getting started](getting-started.md))

**Can I put `[Cache]` on the class instead?**
No. Put it on the interface method. On a class member it does nothing, and warning MC0001 says so; its code fix moves
the attribute to the interface. ([Build diagnostics](user-guide/DIAGNOSTICS.md))

**Where do I call `AddMethodCache()`?**
After you register the services it caches. A cached service registered after it fails when the host starts.
([Registration reference](reference/registration.md#order))

**Why does my method still run on every call?**
The caller uses the class instead of the interface, the call comes from inside the class, or the service was
registered after `AddMethodCache()`. ([Troubleshooting](troubleshooting.md#the-method-runs-on-every-call))

**Which return types can be cached?**
Synchronous values, `Task<T>` and `ValueTask<T>`. Return a list, never a lazy `IEnumerable<T>` or `IQueryable<T>`.
([Generated decorators](features/source-generator.md))

**Are synchronous methods cached in Redis, SQL Server or PostgreSQL?**
No, synchronous methods use the memory cache only, so they never block on the network. Make the method async to share
its results between instances. ([Distributed cache reference](reference/distributed.md#which-calls-use-the-distributed-cache))

## Invalidation

**When does `[CacheInvalidate]` run?**
After the method, or the task it returns, succeeds. When it throws, nothing is invalidated.
([Invalidation](features/invalidation.md))

**The data changes somewhere else (an endpoint, a repository, a message handler). How do I invalidate?**
After the write, call `ICacheManager.InvalidateTagAsync` with a tag from `IMethodCacheKeys.Tag`, which formats it
exactly as the cached method does:
`await cache.InvalidateTagAsync(keys.Tag<IPriceSource>(s => s.GetRateAsync(provider, model), "price:{provider}:{model}"))`.
([When the write happens somewhere else](features/invalidation.md#when-the-write-happens-somewhere-else))

**How do I invalidate after a database transaction commits?**
With Entity Framework Core, `MethodCache.EntityFrameworkCore` does it: tags mapped to an entity type are invalidated
after the commit, and dropped on a rollback. A transaction passed in with `UseTransaction` is invalidated when a
connected context commits it, or when it is over. ([Entity Framework Core](features/entity-framework-core.md#when-the-tags-are-invalidated))
Otherwise invalidate after `CommitAsync` returns, not inside the transaction, and pass `CancellationToken.None`: a
reader between the invalidation and the commit would cache the old data again.
([After a database transaction](features/invalidation.md#after-a-database-transaction))

**How do I make sure no write path forgets to invalidate?**
With Entity Framework Core, map each entity to its tags with `mc.UseEntityFrameworkCore<AppDb>(ef => ef.Entity<Product>("product:{Id}"))`:
every `SaveChanges` that changes one invalidates them, wherever it runs. Otherwise send every write through an
interface whose methods commit their own transaction and carry `[CacheInvalidate]`. Test each write path with a cached
read before and after it. ([Entity Framework Core](features/entity-framework-core.md#mapping-entities-to-tags))

**My endpoint writes with `ExecuteUpdateAsync`. Why the warning MC0027?**
It changes rows of a type you map with `ef.Entity<T>(...)` without EF Core's change tracker, so no entity tags are
known for them. Inside a transaction, add `db.InvalidateAfterCommit(tag)`; without one, call
`ICacheManager.InvalidateTagAsync` after the write. Writes to types you don't map aren't reported, unless the project
maps no type at all. ([Writes the change tracker doesn't see](features/entity-framework-core.md#writes-the-change-tracker-doesnt-see))

**I invalidate through a helper of my own. How do I tell MC0027?**
Mark the helper `[InvalidatesCache]`. A call to it then counts as the invalidation, and the writes inside it aren't
reported. ([Writes the change tracker doesn't see](features/entity-framework-core.md#writes-the-change-tracker-doesnt-see))

**Deleting a user makes the database delete their posts by cascade, but users are saved on every sign-in. What do I map?**
`ef.Entity<User>(user => user.WhenDeleted("posts"))`: those tags are invalidated only when a user is deleted, not
when one is changed. ([Rows the database deletes with a row](features/entity-framework-core.md#rows-the-database-deletes-with-a-row))

**Does the Entity Framework Core package work with Cosmos DB or MongoDB?**
Yes, entity tags and `InvalidateAfterCommit` act on `SaveChanges`. A MongoDB transaction you begin yourself is
invalidated at the context's next save or when it is disposed. ([Document databases](features/entity-framework-core.md#document-databases))

**I update a row from a request without loading it. Is the old category still invalidated?**
Yes. A row saved with `Update`, `Attach` or `Remove(new Product { Id = id })` is read once before the save, so the
tags of its stored values are invalidated too. ([Mapping entities to tags](features/entity-framework-core.md#mapping-entities-to-tags))

**Can I build a tag string myself, such as `$"price:{model}"`?**
Only with `MethodCacheKeys.Part` around each value. MethodCache writes a `Guid` without hyphens (format `N`), an enum
as its number, `bool` as `true`/`false`, numbers and dates in the invariant culture, and escapes `:` and `\` in strings,
so `gemma3:4b` becomes `gemma3\:4b`. A tag formatted differently matches nothing, silently. Analyzer MC0024 warns
about hand-built tags and its code fix adds `MethodCacheKeys.Part`. ([Build diagnostics](user-guide/DIAGNOSTICS.md))

**How do I invalidate one call of a cached method?**
`await cache.InvalidateKeyAsync(keys.For<IProductService>(s => s.GetAsync(42)))`.
([Invalidation](features/invalidation.md#from-code))

**How do I invalidate every entry of one method?**
`await admin.InvalidateMethodAsync(keys.MethodId<IProductService>(s => s.GetAsync(0)))` with `ICacheAdministrator`.
([Invalidation](features/invalidation.md#from-code))

**How do I invalidate everything for a tenant or a user?**
Add a tag such as `"tenant:{tenantId}"` to every method that returns that tenant's data, and invalidate that tag.
([Tag templates](features/invalidation.md#tag-templates))

**Does an invalidation reach my other instances?**
Yes, with Redis, SQL Server or PostgreSQL configured. Other instances drop their copy when the message arrives: at once
with Redis and PostgreSQL, on the next poll (one second) with SQL Server.
([With several instances](features/invalidation.md#with-several-instances))

**How do I pass a version and a cancellation token?**
`InvalidateKeyAsync(key, ct)`, `InvalidateKeyAsync(key, 2, ct)`, `TryGetAsync<Order>(key, ct)` and
`TryGetAsync<Order>(key, 2, ct)`: the token always comes last. ([Manual caching](features/manual-caching.md))

## Expiration and what gets cached

**How do I set how long a result is cached?**
`[Cache(Duration = "00:10:00")]`, or `"30s"`, `"5m"`, `"1.5h"`, `"7d"`. Change it without a rebuild in
`appsettings.json`. ([Writing a duration](features/expiration.md#writing-a-duration),
[Configuration](features/configuration.md#appsettingsjson))

**How do I refresh a value before it expires?**
`RefreshAhead`, shorter than `Duration`: a call near the end of the duration returns the cached value and refreshes it
in the background. ([Refresh-ahead](features/expiration.md#refresh-ahead))

**How do I keep serving the last value when the database is down?**
`FailSafe`: when the method throws or is slower than `FailSafeTimeout`, the last good value is returned.
([Fail-safe](features/fail-safe.md))

**Is `null` cached?**
Yes, by default. `CacheNullValues = false` in `mc.Memory(...)` turns that off for every method. For one method, use
`CacheWhen`: `mc.Method((IProductService s) => s.FindAsync(Any<int>.Value)).CacheWhen(product => product is not null)`.
([Null values](features/memory-cache.md#null-values), [CacheWhen](features/conditional-caching.md#cachewhen))

**How do I cache only some results?**
`CacheWhen` decides per method from the result whether to store it. ([Conditional caching](features/conditional-caching.md))

**How do I skip the cache for one call?**
With manual caching, `WithEnabled(false)` for that call. For a `[Cache]` method, add a second interface method without
`[Cache]` that reads the source directly. ([Skipping the cache for one call](features/conditional-caching.md#skipping-the-cache-for-one-call))

**How do I turn caching off for a method without a rebuild?**
`"Enabled": false` for that method in `appsettings.json`, or `IRuntimeCacheConfigurator` at runtime.
([Configuration](features/configuration.md))

**What does the `Weigher` look like?**
`Func<string, object?, long>`: `(key, value) => weight`, called once when an entry is stored. Set it with
`WeightLimit` to bound the memory cache by size. ([Bounding by size](features/memory-cache.md#bounding-by-size))

**Can callers change a cached object?**
They share the same instance, so a change is seen by every caller. Return immutable types such as records.
([Cached objects are shared](features/memory-cache.md#cached-objects-are-shared))

**Can I cache a `JsonObject`, a `DataTable` or a `Stream`?**
No: every caller gets the same instance at once, and these change while they are read (a `JsonNode` parses lazily
and isn't thread-safe). Warning MC0025 says so. Return a record, a string or a `JsonElement`.
([MC0025](user-guide/DIAGNOSTICS.md#mc0025))

**My service reads the current user from `HttpContext`. Can I cache it?**
Not as it is: the key holds only the method's arguments, so one user's result reaches the next. Pass the user or
tenant id as an argument. Warning MC0026 reports such services. The same goes for row-level security on the
connection. ([Per-user data](getting-started.md#per-user-data))

## Keys

**How are keys built?**
From the assembly, interface, method and the arguments' values. A `CancellationToken` is not part of the key.
([Cache keys](features/keys.md))

**My argument is a class with private fields. What happens?**
Arguments whose only state is private are rejected; implement `ICacheKeyProvider` on the type.
([Types whose state is private](features/keys.md#types-whose-state-is-private))

**How do I cache something without an attribute?**
`ICacheManager.GetOrCreateAsync(key, factory, o => o.WithDuration(...))` with a prefixed key such as `$"order:{id}"`.
([Manual caching](features/manual-caching.md))

**How do I cache a client from a NuGet package?**
`[assembly: CacheInterface(typeof(IClient), nameof(IClient.Method), Duration = "...")]` caches chosen methods of its
own interface. ([Caching third-party code](features/third-party.md))

## Running in production

**How do I share the cache between instances?**
`AddMethodCache(mc => mc.UseRedis("..."))`, `mc.UseSqlServer(...)` or `mc.UsePostgreSql(...)`.
([Distributed cache](features/distributed-cache.md))

**A result type stays in memory only, with a warning. Why?**
It doesn't read back from JSON unchanged: private setters, a derived type returned as its base type, or `object`.
Return records with public properties, or register a serializer with `UseJson(options)`.
([Serialization](features/distributed-cache.md#serialization))

**Can a worker process invalidate the API's memory cache without sharing values?**
Yes: `UsePostgreSql` (or Redis, SQL Server) with `Defaults(o => o.WithDistributed(false))`. Values stay in each
process's memory, and invalidations reach every process. ([Invalidations only](features/distributed-cache.md#invalidations-only))

**What happens when Redis is down?**
Calls keep working from memory and your methods. Each call waits at most one second, Redis is skipped for ten seconds
after a failure, and invalidations are retried. ([Distributed cache](features/distributed-cache.md))

**How do I see what's cached?**
`ICacheAdministrator`: `GetStatsAsync`, `GetMethods`, `ListEntries`. The admin endpoints in `MethodCache.AspNetCore`
show the same. ([Monitoring](features/monitoring.md))

**Why does `POST clear` need a `{}` body?**
The admin `POST` endpoints take only JSON, so a page on another site can't make a signed-in admin's browser call
them. Send `-H "Content-Type: application/json" -d "{}"`.
([ASP.NET Core](../src/MethodCache.AspNetCore/README.md#endpoints))

**How do I get metrics into Prometheus?**
Use OpenTelemetry's Prometheus exporter with `AddMethodCacheInstrumentation()` on the meter builder. The meter is named
`MethodCache`, so `dotnet-counters monitor --counters MethodCache` shows it too.
([Monitoring](features/monitoring.md#standard-net-listeners))

**How do I publish with Native AOT?**
Register your `JsonSerializerContext` with `mc.UseJson(AppJsonContext.Default)`. ([Native AOT](features/native-aot.md))

## Testing

**How do I turn caching off in tests?**
Register `NoOpCacheManager` after `AddMethodCache()`. ([Testing](features/testing.md#turning-method-caching-off))

**A test writes to the database directly. How do I keep the cache from serving old data?**
Call `ICacheAdministrator.ClearAsync()` after the write, or invalidate the affected tag with `IMethodCacheKeys.Tag`.
([Testing](features/testing.md#when-a-test-writes-to-the-database-directly))

**How do I test expiration without waiting?**
Register a fake `TimeProvider`. ([Testing](features/testing.md#time))

## Coding agents

**How do I have an agent add MethodCache to my project?**
Give it the prompt in the README's [Using a coding agent](../README.md#using-a-coding-agent). The `MethodCache` package
carries these docs in its `docs` folder, and [agent instructions](ai/methodcache.md) has the rules to copy into your
agent's instructions.
