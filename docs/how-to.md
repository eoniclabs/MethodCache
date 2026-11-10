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

**Can I keep a lock, an idempotency record or an "already sent" marker in MethodCache?**
No. Any entry can disappear (expiry, eviction, invalidation, a lost backplane connection), and a failed write to the
distributed cache is only logged, so a lost marker repeats the action. Keep such state in a store with atomic writes,
such as a database row or Redis `SET NX`. ([State that isn't a cache](migration-from-imemorycache.md#state-that-isnt-a-cache))

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

**A tag is only cleared by an admin, or simply expires. How do I stop MC0028 without a suppression?**
List it in `[Cache(Tags = ["food-catalog"], InvalidatedManually = ["food-catalog"])]`. MC0028 and `TagCoverage` count
it as invalidated without looking for an invalidation, so neither a `SuppressMessage` nor `alsoInvalidatedBy` is
needed. ([Cleared by hand](features/invalidation.md#cleared-by-hand))

**My sliding window seems to do nothing. Why?**
It is as long as the `Duration` or longer. An entry never lives past its `Duration`, so `WithSlidingExpiration` of a
day with a `Duration` of five minutes from `Defaults` expires each entry after five minutes. Make the window shorter
than the `Duration`, or raise the `Duration`. Warning MC0038 reports it on one `[Cache]`, and the host logs a warning
that names the method when it starts. ([Sliding expiration](features/expiration.md#sliding-expiration), [MC0038](user-guide/DIAGNOSTICS.md#mc0038))

**How do I keep an entry for as long as it is read, like an `IMemoryCache` entry with only `SlidingExpiration`?**
Set only the window: `[Cache(SlidingExpiration = "30m")]`. Without a `Duration` the entry is kept while it is read at
least once per window, up to `MaxExpiration` (7 days, raise it with `mc.Memory(o => o.MaxExpiration = ...)`). A
`Duration` from `Defaults` or a group still ends it, so then give the method its own long `Duration`. With a
distributed cache the stored copy lives one window from when it was stored, so another instance that asks for the
value after that loads it again, and the new value replaces the copies on every instance.
([Kept as long as it is read](features/expiration.md#kept-as-long-as-it-is-read))

**How long does an entry live when I set nothing?**
Five minutes, in memory and in the distributed cache. No entry lives past `MaxExpiration` (7 days), and the distributed
cache keeps an entry at most `TagRetention` (1 day) from when its load started.
([Duration](features/expiration.md#duration))

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

**Does `Defaults` apply to `GetOrCreateAsync`?**
No. `Defaults` and groups are for cached methods; a manual call has only the options it passes. Without `WithDuration`
its entry lives five minutes, and with only `WithSlidingExpiration` only `MaxExpiration` limits how long reads keep it.
([Manual caching](features/manual-caching.md), [Sliding expiration](features/expiration.md#sliding-expiration))

**How do I refresh a value before it expires?**
`RefreshAhead`, shorter than `Duration`: a call near the end of the duration returns the cached value and refreshes it
in the background. With a distributed cache the new value replaces the stored copy, so other instances read it instead
of loading it. ([Refresh-ahead](features/expiration.md#refresh-ahead))

**How do I keep serving the last value when the database is down?**
`FailSafe`: when the method throws or is slower than `FailSafeTimeout`, the last good value is returned. Each instance
serves the value it has in memory, also one it read from the distributed cache; an instance that never had the value
gets the exception. ([Fail-safe](features/fail-safe.md))

**Is `null` cached?**
Yes, by default. `CacheNullValues = false` in `mc.Memory(...)` turns that off for every method. For one method, use
`CacheWhen`: `mc.Method((IProductService s) => s.FindAsync(Any<int>.Value)).CacheWhen(product => product is not null)`.
([Null values](features/memory-cache.md#null-values), [CacheWhen](features/conditional-caching.md#cachewhen))

**How do I cache a token until it expires, or an empty result for a shorter time?**
`DurationFor` gives each result its own duration: `mc.Method((ITokenClient c) => c.GetTokenAsync(Any<string>.Value))
.DurationFor(token => token.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1))`. Zero or less doesn't cache
the result. ([A duration from the result](features/expiration.md#a-duration-from-the-result))

**My method returns null when there is no row yet, but one takes effect at a known time. How long is null kept?**
`DurationFor` gets the null too, and null can't say when the row starts. Return a type that can, such as
`record CurrentPrice(Price? Price, DateTimeOffset? NextChangeAt)`, and keep it until `NextChangeAt`; or give null a
fixed duration with `price => price is null ? TimeSpan.FromMinutes(5) : ...`.
([A duration from the result](features/expiration.md#a-duration-from-the-result))

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
as `float[]` embeddings in bytes, and any other value as 64; `MethodCacheWeigher.Bytes(otherValue: 1024)` weighs other
values as 1024. ([Bounding by size](features/memory-cache.md#bounding-by-size))

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

**My cached read uses a table without row-level security, through a context that has the request's tenant. Why MC0026?**
The analyzer can't tell which tables a method reads. When the result depends only on the arguments, such as an API key
looked up by its hash, suppress MC0026 on that class with the reason.
([A table the request doesn't filter](user-guide/DIAGNOSTICS.md#a-table-the-request-doesnt-filter))

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

**An argument holds a secret, such as a URL with a SAS token. How do I keep it out of the key?**
Keys hold arguments in plain text, and they show up in the admin endpoints, the distributed cache and traces. Pass an
id where you can; otherwise wrap the value in a type whose `ICacheKeyProvider` returns a SHA-256 hash of all of it,
token included, so a caller with another token never gets this caller's result.
([Secrets in arguments](features/keys.md#secrets-in-arguments))

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
`dotnet_diagnostic.<id>.severity = warning`. Without warnings as errors, leave it unset so each upgrade shows its new
warnings. Every rule up to 1.0.0 counts as a rule of 1.0. ([Rules of a later version](user-guide/DIAGNOSTICS.md#rules-of-a-later-version))

**Can the MethodCache packages in a project have different versions?**
No: use one version of all of them. Each add-on requires the exact `MethodCache` version it was built with, and NuGet
warns with NU1608 when they differ. ([Recommended project setup](getting-started.md#recommended-project-setup))

## Running in production

**How do I share the cache between instances?**
`AddMethodCache(mc => mc.UseRedis("..."))`, `mc.UseSqlServer(...)` or `mc.UsePostgreSql(...)`.
([Distributed cache](features/distributed-cache.md))

**How do I keep some values on one instance, with no network call at all?**
`WithDistributed(false)` on the call (or `[Cache(Distributed = false)]` on the method): `GetOrCreateAsync` and
`SetAsync` then use only this instance's memory, so no second container or cache is needed. Invalidating the key or a
tag still reaches every instance. ([Manual caching](features/manual-caching.md#storing-a-value-you-already-have))

**Does a runtime override reach every instance and survive a restart?**
Yes, with Redis, SQL Server or PostgreSQL and their backplane: `IRuntimeCacheConfigurator.UpsertAsync` stores it in the
distributed cache, the other instances apply it within moments, and an instance that starts applies the active ones.
Such an override can't set `CacheWhen`, `DurationFor`, `TagsFor` or `SlidingFor`. Without a distributed cache, or with
`EnableBackplane = false`, it stays on its own instance until a restart.
([Runtime overrides](features/configuration.md#runtime-overrides))

**Right after the app starts, the first calls don't use Redis. Is something wrong?**
No. The connection opens in the background once the host has started, and until the distributed cache first answers,
a call waits at most `OperationTimeout` (one second) and then uses memory only. A slow start doesn't open the circuit
breaker; a connection that fails or takes over 30 seconds does, and is logged as a warning. In a console app without a
generic host, the first call opens the connection. ([When the distributed cache is down](features/distributed-cache.md#when-the-distributed-cache-is-down))

**How do I see how long a method's entries live, whether they reach Redis, and what is in the key?**
`ICacheAdministrator.GetMethods()`, or `GET /methodcache/methods` with `MapMethodCacheAdmin`: each method's
`Lifetime` (defaults filled in, and how long the shared copy lives), `Storage` (where values go, and why only in memory),
`KeyParameters`, and `NotShared`, the result types this instance kept in memory with the reason and count.
`SettingSources` names where each setting comes from, and `ReplacedSources` a setting one source replaces in another,
such as a `DurationFor` in startup code that replaces the `Duration` of the method's `[Cache]`.
([What applies on this instance](features/configuration.md#what-applies-on-this-instance))

**A value lives longer in Redis than I asked for, or shorter. Why?**
The copy in the distributed cache lives the `Duration`, at most one sliding window, and at most `TagRetention` (one day)
less two seconds, so that a tag invalidation always outlives it. The first value of each method, or of each `WithName`
name, that asks for longer than `TagRetention` allows logs a warning; raise `TagRetention` to share values longer. A
`Duration` or a sliding window longer than `MaxExpiration` (7 days) fails where it is set.
([How long an entry lives](features/expiration.md#how-long-an-entry-lives))

**How do I keep large, rarely read values in Redis without filling every instance's memory?**
Bound memory by size: set a `Weigher` and a `WeightLimit` with `mc.Memory(...)`. When the limit is reached, memory evicts
the entries read least often first, and the next read of one gets it from the distributed cache, not from your method.
There is no separate duration for memory. ([Bounding by size](features/memory-cache.md#bounding-by-size))

**A result type stays in memory only, with a warning. Why?**
It doesn't read back from JSON unchanged: private setters, `object`, a dictionary made with its own comparer, or a
derived type MethodCache can't name (MC0039). Return records with public properties, or register a serializer with
`UseJson(options)`.
([Serialization](features/distributed-cache.md#serialization))

**Does a `Dog` in a `List<Animal>` come back as a `Dog`?**
Yes. MethodCache's generator names the classes that derive from a type a cached result declares to its JSON and
MessagePack serializers, in your project and the projects around it. Under Native AOT, add `[JsonSerializable(typeof(Dog))]`
to the context you pass to `UseJson`; MC0040 and its code fix point it out.
([Derived types](features/distributed-cache.md#derived-types))

**I renamed a property of a cached result. Do I need to raise `Version`?**
No. The generator computes a fingerprint of the result's shape, so the new build reads entries of its own, and
invalidations still reach both builds during a rolling deploy. Raise `Version` only for changes the build can't see,
such as what a value means.
([Changing result types](features/distributed-cache.md#changing-result-types-and-upgrading))

**Can I change a list a cached method returned?**
Change a copy (`result.ToList()`, or a record's `with`). The memory cache hands the same instance to later callers, so
a change shows up there but not on other instances. MC0043 reports such code, and in Development a warning names the
method when a cached value has changed.
([Results that don't change](features/distributed-cache.md#results-that-dont-change))

**Can a worker process invalidate the API's memory cache without sharing values?**
Yes: `UsePostgreSql` (or Redis, SQL Server) with `Distributed(o => o.StoreValuesByDefault = false)` in every process. Values
stay in each process's memory, from cached methods and manual calls alike, and an invalidation in one process reaches all of them: through `LISTEN`/`NOTIFY` with
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
meter adapter, or no series appear, and `InstrumentFilterPredicate = i => i.Meter.Name == MethodCacheTelemetry.Name`
to leave out the process's other meters. ([Monitoring](features/monitoring.md#standard-net-listeners))

**The `methodcache_method` label is too long for my dashboard. Can I shorten it?**
It is the full method id, so methods with the same name in different assemblies never share a series. Copy the
interface and method into a label of their own with `label_replace` in the query, or with `metric_relabel_configs`
when Prometheus scrapes. ([Hit ratio per method](features/monitoring.md#hit-ratio-per-method))

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
isn't wrapped by its caching decorator. When the services need a database or a key vault to resolve,
`CachedServices.RegistrationProblems(provider)` checks the registrations only. Both throw when the provider has no
cached service. ([Every cached service resolves](features/testing.md#every-cached-service-resolves))

**How do I catch a cached service that the application never registers?**
Compare `CachedServices.Registered(provider)`, the registered services with cached methods ordered by full name, with
the list the application should have: `Assert.Equal([typeof(IOrders), typeof(IPrices)], CachedServices.Registered(provider))`.
To keep that list up to date by itself, compare with the interfaces the project declares:
`Assert.Equal(CachedServices.Declared(typeof(IOrders).Assembly), CachedServices.Registered(provider))`. Pass the
assembly of each project with cached interfaces the application registers; for a shared library that declares
interfaces other applications register, add the ones this application uses by hand.
`ProblemsAsync` can't see a service with no registration. ([Every cached service resolves](features/testing.md#every-cached-service-resolves))

**How do I test how a method is cached: its duration, its key, whether it is shared?**
`CachedServices.Method<IPrices>(provider, p => p.GetAsync(""))` from `MethodCache.Testing` returns what `GetMethods()`
reports for that method, found by a call, so a rename still finds it. To see every change in review, serialize
`GetMethods()` to JSON and compare it with a file in the repository.
([How each method is cached](features/testing.md#how-each-method-is-cached))

**How do I check that every tenant's values have the tenant in the key?**
Loop over `GetMethods()` and assert that `KeyParameters` starts with the tenant parameter, except for the methods that
serve every tenant. ([A load for another tenant fails](features/testing.md#a-load-for-another-tenant-fails))

**How do I test that a cached method refuses another tenant's id?**
Set the request's tenant to one tenant, call the method with another tenant's id, and assert that the `[ChecksRequestState]`
check throws. The check runs on a load, so nothing is cached under the other tenant's key.
([A load for another tenant fails](features/testing.md#a-load-for-another-tenant-fails))

**How do I check that every cache tag is invalidated somewhere?**
`Assert.Empty(TagCoverage.Uninvalidated(provider))` from `MethodCache.Testing`. It counts `[CacheInvalidate]` methods,
the EF Core mapping and the invalidations in the projects the test project references, such as a worker's
`InvalidateTagAsync` calls. A tag marked `InvalidatedElsewhere` counts only when one of those projects invalidates it,
and one marked `InvalidatedManually` always counts. Pass tags invalidated outside the solution in `alsoInvalidatedBy`. Analyzer MC0028 checks one project while you build.
([Every tag has an invalidation](features/testing.md#every-tag-has-an-invalidation))

**How do I test expiration without waiting?**
Register a fake `TimeProvider`; `AddMethodCacheTesting()` registers `FakeTimeProvider`. ([Testing](features/testing.md#time))

## Coding agents

**How do I have an agent add MethodCache to my project?**
Give it the prompt in the README's [Using a coding agent](../README.md#using-a-coding-agent). The `MethodCache` package
carries these docs in its `docs` folder, and [agent instructions](ai/methodcache.md) has the rules to copy into your
agent's instructions.

**How does an agent report a problem with MethodCache?**
It mentions the problem in its report and offers to draft feedback; it never sends any on its own. When you ask, it
drafts a GitHub issue titled `[Agent feedback] ...` with what it tried, what happened, a minimal example and the
versions, never with your code, names, secrets or other sensitive information, and files it when you approve. We
appreciate every report. ([Agent instructions](ai/methodcache.md#feedback))
