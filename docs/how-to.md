[← MethodCache documentation](README.md)

# How do I…?

Short answers to common questions, each with a link to the page that has the details. Search this page for a word
from your question.

## Getting started

**Why does MethodCache need an interface?**
The source generator writes a class that implements your interface, checks the cache and calls your implementation
on a miss. Dependency injection hands out that class in place of yours. A class without an interface can't be
replaced that way: add an interface, or use [manual caching](features/manual-caching.md) with `ICacheManager`.
Adding one changes the registrations, constructors and tests that name the class.
([A class without an interface](getting-started.md#a-class-without-an-interface))

**Can I put `[Cache]` on the class instead?**
No. Put it on the interface method. On a class member it does nothing, and warning MC0001 says so; its code fix moves
the attribute to the interface. ([Build diagnostics](user-guide/DIAGNOSTICS.md))

**Where do I call `AddMethodCache()`?**
After you register the services it caches. A cached service registered after it fails when the host starts. A
test that replaces a cached service with a fake calls `AddMethodCache()` again after the fake
([Testing](features/testing.md#replacing-a-cached-service-with-a-fake)). A
program with cached services gets warning MC0030 when it calls neither `AddMethodCache` nor a setup method of a
referenced project that calls it.
([Registration reference](reference/registration.md#order))

**Why does my method still run on every call?**
The caller uses the class instead of the interface, the call comes from inside the class (warning MC0031), the class
was created with `new`, or the service was registered after `AddMethodCache()`.
([Troubleshooting](troubleshooting.md#the-method-runs-on-every-call))

**Can several applications share startup code or `appsettings.json` that configures cached methods?**
Yes. A setting for a `[Cache]` method whose service an application doesn't register is skipped there, and logged at
Information at startup; only an id that matches no `[Cache]` method fails. ([Configuration](features/configuration.md))

**My cached service lives in another project of the solution. Does it need its own MethodCache reference?**
No. The project gets the generator through the project it references. If the generator doesn't run there,
`AddMethodCache` fails and names the project. ([Troubleshooting](troubleshooting.md#exceptions))

**Which return types can be cached?**
Synchronous values, `Task<T>` and `ValueTask<T>`. Return a list, never a lazy `IEnumerable<T>` or `IQueryable<T>`.
([Generated decorators](features/source-generator.md))

**Are synchronous methods cached in Redis, SQL Server or PostgreSQL?**
No, synchronous methods use the memory cache only, so they never block on the network. Make the method async to share
its results between instances. ([Distributed cache reference](reference/distributed.md#which-calls-use-the-distributed-cache))

**What changed in a new version, and what do I change when I upgrade?**
`CHANGELOG.md` lists each version's changes, and its "Upgrading" part says what to change in your code and which
keys change. The `MethodCache` package carries it in its `docs` folder next to these pages.
([CHANGELOG](https://github.com/eoniclabs/MethodCache/blob/main/CHANGELOG.md))

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
`ICacheManager.InvalidateTagAsync` after the write. Writes to types you don't map aren't reported, unless neither the
project nor a project it references maps a type. ([Writes the change tracker doesn't see](features/entity-framework-core.md#writes-the-change-tracker-doesnt-see))

**My test project reports MC0027 when it sets up test data. Why?**
A test project checks its writes against the mapping of the project it tests, so only a write to a table that project
maps is reported: cached results that read those rows would be stale for the rest of the test. Invalidate the tags,
or turn the warning off for that line with `#pragma warning disable MC0027` when nothing cached reads the rows.
([MC0027](user-guide/DIAGNOSTICS.md#mc0027))

**I invalidate through a helper of my own. How do I tell MC0027?**
Mark the helper `[InvalidatesCache]`. A call to it then counts as the invalidation, and the writes inside it aren't
reported. ([Writes the change tracker doesn't see](features/entity-framework-core.md#writes-the-change-tracker-doesnt-see))

**Why the warning MC0028 on a tag?**
Nothing in the project invalidates it, so its entries stay until they expire. Invalidate it where the data changes,
with `[CacheInvalidate]`, an entity mapping, `InvalidateTagAsync` or `InvalidateAfterCommit`. When another application
invalidates it, such as a worker, list it in `[Cache(InvalidatedElsewhere = ["catalog"])]`; MC0028 then counts it,
and `TagCoverage` counts it when the test project references the worker that invalidates it. ([MC0028](user-guide/DIAGNOSTICS.md#mc0028), [In another application](features/invalidation.md#in-another-application))

**Deleting a user makes the database delete their posts by cascade, but users are saved on every sign-in. What do I map?**
`ef.Entity<User>(user => user.WhenDeleted("posts"))`: those tags are invalidated only when a user is deleted, not
when one is changed. ([Rows the database deletes with a row](features/entity-framework-core.md#rows-the-database-deletes-with-a-row))

**Does the Entity Framework Core package work with Cosmos DB or MongoDB?**
Yes, entity tags and `InvalidateAfterCommit` act on `SaveChanges`. A MongoDB transaction you begin yourself is
invalidated at the context's next save or when it is disposed. ([Document databases](features/entity-framework-core.md#document-databases))

**I update a row from a request without loading it. Is the old category still invalidated?**
Yes. A row saved with `Update`, `Attach` or `Remove(new Product { Id = id })` is read once before the save, so the
tags of its stored values are invalidated too. ([Mapping entities to tags](features/entity-framework-core.md#mapping-entities-to-tags))

**With row-level security, what if the user can't read the row that is saved without loading it?**
Then the values your code set are used for the old tags. PostgreSQL and SQL Server don't let the save change that row
either: it changes nothing and throws `DbUpdateConcurrencyException`. EF Core query filters don't hide the row from
that read. ([Saves of rows the context didn't load](features/entity-framework-core.md#saves-of-rows-the-context-didnt-load))

**Can I build a tag string myself, such as `$"price:{model}"`?**
Yes, in `MethodCacheKeys.Tag`: `MethodCacheKeys.Tag($"price:{model}")`. It writes each value the way tags do: a `Guid`
without hyphens (format `N`), an enum as its number, `bool` as `true`/`false`, numbers and dates in the invariant
culture, and `:` and `\` escaped in strings, so `gemma3:4b` becomes `gemma3\:4b`. Plain interpolation formats them
differently and matches nothing, silently. Analyzer MC0024 warns about such tags and its code fix wraps them in
`MethodCacheKeys.Tag`. `MethodCacheKeys.Part` formats one value. ([Build diagnostics](user-guide/DIAGNOSTICS.md#mc0024))

**I just saved the new value. Can I put it in the cache instead of invalidating?**
For a key of your own, yes: `await cache.SetAsync($"order:{saved.Id}", saved, o => o.WithDuration(TimeSpan.FromMinutes(30)))`.
It replaces the value in memory and the distributed cache, other instances drop their copy, and a load already running
for the key doesn't overwrite it. Use the options of the calls that read the key. For a `[Cache]` method, invalidate
its key or tags. ([Storing a value you already have](features/manual-caching.md#storing-a-value-you-already-have))

**A tag depends on the result, not the arguments, such as the customer of an order found by its id. How?**
`mc.Method((IOrderService s) => s.GetAsync(Any<int>.Value)).TagsFor(order => [MethodCacheKeys.Tag($"customer:{order.CustomerId}")])`
adds the result's tags to the method's own, so invalidating `customer:7` removes the order too. For manual caching, use
`o => o.TagsFor<Order>(...)`. ([Tags from the result](features/invalidation.md#tags-from-the-result))

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

**Which token does a `GetOrCreateAsync` factory use?**
Its own: `async ct => await orders.GetAsync(id, ct)`, with the caller's token passed as `cancellationToken: ct`. A
token from outside the factory, such as `HttpContext.RequestAborted`, fails the load for every caller sharing it when
it is cancelled; warning MC0035 reports it, with a code fix. ([Things to know](features/manual-caching.md#things-to-know))

## Expiration and what gets cached

**How do I set how long a result is cached?**
`[Cache(Duration = "10m")]`, with a number and a unit: `"30s"`, `"5m"`, `"1.5h"`, `"7d"`. Change it without a rebuild in
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

**How do I cache a token until it expires, or an empty result for a shorter time?**
`DurationFor` gives each result its own duration: `mc.Method((ITokenClient c) => c.GetTokenAsync(Any<string>.Value))
.DurationFor(token => token.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1))`. Zero or less doesn't cache
the result. ([A duration from the result](features/expiration.md#a-duration-from-the-result))

**How do I let successful results slide and keep failed ones for a fixed time?**
Add `SlidingFor` next to `DurationFor`: `.DurationFor(r => r.Succeeded ? TimeSpan.FromDays(1) : TimeSpan.FromHours(2))
.SlidingFor(r => r.Succeeded ? TimeSpan.FromMinutes(30) : TimeSpan.Zero)`. Zero turns sliding off for that result.
([A sliding window from the result](features/expiration.md#a-sliding-window-from-the-result))

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
`WeightLimit` to bound the memory cache by size. `o.Weigher = MethodCacheWeigher.Bytes` weighs strings and arrays such
as `float[]` embeddings in bytes, and any other value as 64. ([Bounding by size](features/memory-cache.md#bounding-by-size))

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

**My cached method takes the tenant id and checks it against the request's tenant. Why the warning MC0026?**
The analyzer can't see the check. Mark your check method `[ChecksRequestState]`, such as `EnsureActsFor(tenantId)`, and
call it with the parameter in each cached method; the class is then not reported. Called through an interface, mark
the interface method. The check runs only when a value is loaded, so the caller must still take the id from the request.
([A tenant parameter checked against the request](user-guide/DIAGNOSTICS.md#a-tenant-parameter-checked-against-the-request))

**I renamed my `ICurrentUser`. Does MC0026 still find it?**
Not until you update `methodcache.request_state_types` in `.editorconfig`. Warning MC0036 reports a name there that
matches no type. ([MC0036](user-guide/DIAGNOSTICS.md#mc0036))

**My database uses row-level security on the connection. How do I cache reads?**
Give the cached reads a context of their own from `IDbContextFactory<T>`, not the one set up for the signed-in user,
and pass what the policies filter on, such as the tenant id, as an argument. Check in the endpoint that the user may
read it. ([Row-level security](features/entity-framework-core.md#row-level-security))

## Keys

**How are keys built?**
From the assembly, interface, method and the arguments' values. A `CancellationToken` is not part of the key.
([Cache keys](features/keys.md))

**My argument is a class with private fields. What happens?**
Arguments whose only state is private are rejected, at build time with error MC0034; implement `ICacheKeyProvider` on
the type.
([Types whose state is private](features/keys.md#types-whose-state-is-private))

**Which methods get a key that is written when the call runs?**
Those with an argument of type `object`, an interface the decorator can't write or a collection of collections.
Information MC0037 marks them in the editor; pass simple values or a sealed record to have the key written when you
build. ([MC0037](user-guide/DIAGNOSTICS.md#mc0037))

**How do I cache something without an attribute?**
`ICacheManager.GetOrCreateAsync(key, factory, o => o.WithDuration(...))` with a prefixed key such as `$"order:{id}"`.
([Manual caching](features/manual-caching.md))

**Two applications share Redis. Do they share cached values?**
Under the same `KeyPrefix` they share entries and invalidations, which is how one invalidates the other's. `[Cache]`
keys include the method's assembly and interface, but keys passed to `ICacheManager` don't, so the same manual key is
one entry in both. ([Several applications on one store](features/distributed-cache.md#several-applications-on-one-store))

**I need several instances of a cached class, one per provider. How?**
Register each as a keyed service before `AddMethodCache`, such as `AddKeyedSingleton<IRates>("ecb", ...)`, and inject
it with `[FromKeyedServices("ecb")]`. Each caches its own results. An instance created with `new` isn't cached
(MC0032). ([Keyed registrations](reference/registration.md#keyed-registrations))

**How do I load many ids with one query, caching each one?**
`ICacheManager.GetOrCreateManyAsync<int, User>(ids, id => $"user:{id}", (missingIds, ct) => ..., options)`. The
factory gets only the ids that aren't cached and returns a dictionary by id; an id it leaves out is not cached and not
returned. ([Many keys at once](features/manual-caching.md#many-keys-at-once))

**How do I cache a client from a NuGet package?**
`[assembly: CacheInterface(typeof(IClient), nameof(IClient.Method), Duration = "...")]` caches chosen methods of its
own interface. ([Caching third-party code](features/third-party.md))

## Upgrading

**A new MethodCache version brings new warnings, and we treat warnings as errors. How do we upgrade?**
Set `<MethodCacheAnalysisLevel>1.0</MethodCacheAnalysisLevel>` (the version whose rules you want) in
`Directory.Build.props`: rules that came later are off until you raise it. Turn one on in `.editorconfig` with
`dotnet_diagnostic.<id>.severity = warning`. ([Rules of a later version](user-guide/DIAGNOSTICS.md#rules-of-a-later-version))

## Running in production

**How do I share the cache between instances?**
`AddMethodCache(mc => mc.UseRedis("..."))`, `mc.UseSqlServer(...)` or `mc.UsePostgreSql(...)`.
([Distributed cache](features/distributed-cache.md))

**Does a runtime override reach every instance and survive a restart?**
Yes, with Redis, SQL Server or PostgreSQL and their backplane: `IRuntimeCacheConfigurator.UpsertAsync` stores it in the
distributed cache, the other instances apply it within moments, and an instance that starts applies the active ones.
Such an override can't set `CacheWhen`, `DurationFor`, `TagsFor` or `SlidingFor`. Without a distributed cache, or with
`EnableBackplane = false`, it stays on its own instance until a restart.
([Runtime overrides](features/configuration.md#runtime-overrides))

**A result type stays in memory only, with a warning. Why?**
It doesn't read back from JSON unchanged: private setters, a derived type returned as its base type, or `object`.
Return records with public properties, or register a serializer with `UseJson(options)`.
([Serialization](features/distributed-cache.md#serialization))

**Can a worker process invalidate the API's memory cache without sharing values?**
Yes: `UsePostgreSql` (or Redis, SQL Server) with `Defaults(o => o.WithDistributed(false))` in every process. Values
stay in each process's memory, and an invalidation in one process reaches all of them: through `LISTEN`/`NOTIFY` with
PostgreSQL, pub/sub with Redis, a table polled every second with SQL Server. The same goes for several instances of
one app. With EF Core in the worker, its saves invalidate the tags its mapping names.
([A worker and an API](features/distributed-cache.md#a-worker-and-an-api))

**What happens when Redis is down?**
Calls keep working from memory and your methods. Each call waits at most one second, Redis is skipped for ten seconds
after a failure, and invalidations are retried. ([Distributed cache](features/distributed-cache.md))

**How do I check that every instance uses the same distributed cache?**
Each instance logs one `Information` line when its cache starts, naming `MaxItems`, the distributed cache, its
`KeyPrefix` and the backplane, or that there is no distributed cache. ([Monitoring](features/monitoring.md#which-layers-an-instance-uses))

**How do I see what's cached?**
`ICacheAdministrator`: `GetStatsAsync`, `GetMethods`, `ListEntries`. The admin endpoints in `MethodCache.AspNetCore`
show the same. ([Monitoring](features/monitoring.md))

**How do I log who cleared or invalidated the cache through the admin endpoints?**
Pass `audit` to `MapMethodCacheAdmin`: `app.MapMethodCacheAdmin("CacheAdmins", audit: (action, ct) => ...)`. It runs
before each change with `action.User`, `action.Kind`, `action.Targets` and `action.Time`; when it throws, the request
fails and nothing changes. ([ASP.NET Core](../src/MethodCache.AspNetCore/README.md#audit))

**Why does `POST clear` need a `{}` body?**
The admin `POST` endpoints take only JSON, so a page on another site can't make a signed-in admin's browser call
them. Send `-H "Content-Type: application/json" -d "{}"`.
([ASP.NET Core](../src/MethodCache.AspNetCore/README.md#endpoints))

**How do I get metrics into Prometheus?**
Use OpenTelemetry's Prometheus exporter with `AddMethodCacheInstrumentation()` on the meter builder. The meter is named
`MethodCache`, so `dotnet-counters monitor --counters MethodCache` shows it too. An app that already serves
prometheus-net's `/metrics` gets the instruments with no setup, named like `methodcache_methodcache_requests`. With a
`CollectorRegistry` of your own, set both `Registry` and `MetricFactory = Metrics.WithCustomRegistry(registry)` on the
meter adapter, or no series appear. ([Monitoring](features/monitoring.md#standard-net-listeners))

**How do I publish with Native AOT?**
Register your `JsonSerializerContext` with `mc.UseJson(AppJsonContext.Default)`. ([Native AOT](features/native-aot.md))

## Testing

**How do I turn caching off in tests?**
Register `NoOpCacheManager` after `AddMethodCache()`. ([Testing](features/testing.md#turning-method-caching-off))

**A test writes to the database directly. How do I keep the cache from serving old data?**
Call `ICacheAdministrator.ClearAsync()` after the write, or invalidate the affected tag with `IMethodCacheKeys.Tag`.
([Testing](features/testing.md#when-a-test-writes-to-the-database-directly))

**How do I count how often my implementation ran, or see what was invalidated?**
Add `MethodCache.Testing` and call `services.AddMethodCacheTesting()`. `CacheRecorder.Loads<T>(call)` counts the runs of
one call and `InvalidatedTags` lists the invalidated tags. ([Counting loads and invalidations](features/testing.md#counting-loads-and-invalidations))

**My test changes a row through EF Core and reads it again. How do I prove the read was cached?**
The save invalidates, so the read is fresh either way. Count the runs with `recorder.Loads`, or change the row with SQL
or a context that isn't connected to MethodCache. ([Proving that a result is cached](features/testing.md#proving-that-a-result-is-cached))

**How do I check in a test that every cached service is registered right?**
`Assert.Empty(await CachedServices.ProblemsAsync(provider))` from `MethodCache.Testing`. It resolves each registered service with
cached methods and reports one registered after `AddMethodCache`, one that throws when it is resolved, and one that
isn't wrapped by its caching decorator. ([Every cached service resolves](features/testing.md#every-cached-service-resolves))

**How do I check that every cache tag is invalidated somewhere?**
`Assert.Empty(TagCoverage.Uninvalidated(provider))` from `MethodCache.Testing`. It counts `[CacheInvalidate]` methods,
the EF Core mapping and the invalidations in the projects the test project references, such as a worker's
`InvalidateTagAsync` calls. A tag marked `InvalidatedElsewhere` counts only when one of those projects invalidates it.
Pass tags invalidated outside the solution in `alsoInvalidatedBy`. Analyzer MC0028 checks one project while you build.
([Every tag has an invalidation](features/testing.md#every-tag-has-an-invalidation))

**How do I test expiration without waiting?**
Register a fake `TimeProvider`; `AddMethodCacheTesting()` registers `FakeTimeProvider`. ([Testing](features/testing.md#time))

## Coding agents

**How do I have an agent add MethodCache to my project?**
Give it the prompt in the README's [Using a coding agent](../README.md#using-a-coding-agent). The `MethodCache` package
carries these docs in its `docs` folder, and [agent instructions](ai/methodcache.md) has the rules to copy into your
agent's instructions.

**How does an agent report a problem with MethodCache?**
It drafts a GitHub issue titled `[Agent feedback] ...` with what it tried, what happened, a minimal example and the
versions, never with your code, names, secrets or other sensitive information. It shows you the draft and files it
only when you agree. ([Agent instructions](ai/methodcache.md#feedback))
