# Changelog

Changes that a user of the packages notices. The release steps are in
[docs/developer/RELEASE.md](docs/developer/RELEASE.md); when you tag a release, copy its section into the GitHub
release.

## 1.0.0-rc.14 (2026-11-08)

### Upgrading from 1.0.0-rc.13

- A result that holds a derived type where its base type is declared, such as a `Dog` in a `List<Animal>` or in an
  `Animal` property, or `object` values, such as those of a `Dictionary<string, object>`, now stays in memory with a
  warning instead of reaching the other instances without its data. To share it, add `[JsonDerivedType]` to the base
  type, or return a type without `object`.
- If you followed "Invalidations only" with `Defaults(o => o.WithDistributed(false))`, switch to
  `Distributed(o => o.StoreValues = false)`. `Defaults` applies to cached methods only, so `GetOrCreateAsync` and
  `SetAsync` still stored their values in the distributed cache.

### Added

- `DistributedCacheOptions.StoreValues = false` keeps every value in memory, for cached methods and manual calls alike,
  whatever their options say, and uses Redis, SQL Server or PostgreSQL for invalidations only
  ([Invalidations only](docs/features/distributed-cache.md#invalidations-only)).
- `CachedServices.Declared(assemblies)` in `MethodCache.Testing` lists the interfaces with cached methods in the
  assemblies you pass, ordered as `Registered` orders them. Compare the two, and a test covers a new cached interface
  without a change ([Every cached service resolves](docs/features/testing.md#every-cached-service-resolves)).

### Fixed

- The check before a value is shared through the distributed cache compares the types inside it too, through its
  collections, properties and fields. A `Dog` in a `List<Animal>` came back as a plain `Animal` on the other
  instances, and an `int` in a `Dictionary<string, object>` as a `JsonElement`; such a value now stays in memory with
  the warning ([Serialization](docs/reference/distributed.md#serialization)).
- MC0020 no longer reports an interface or abstract class with `[JsonDerivedType]`, `[JsonPolymorphic]` or `[Union]`,
  whose results the serializer restores.
- Setting `MethodCacheAnalysisLevel` failed every build with MSB4184 since 1.0.0-rc.11. It builds again.

### Documentation

- Testing: a minimal API endpoint that takes a cached interface fails the host before `Registered` runs when the
  interface isn't registered.
- The agent instructions no longer ask a coding agent to draft feedback by itself: it mentions a problem in its
  report and offers to draft feedback, and drafts and files it only when its user asks. They say we appreciate it.

## 1.0.0-rc.13 (2026-11-07)

### Added

- `CachedServices.Registered(provider)` in `MethodCache.Testing` lists the registered services with cached methods,
  so a test can compare it with the services the application should have and catch one that is never registered,
  which `ProblemsAsync` and `RegistrationProblems` can't see
  ([Every cached service resolves](docs/features/testing.md#every-cached-service-resolves)).

### Documentation

- Expiration and manual caching: `Defaults` and groups apply to cached methods, not to manual calls. A manual call
  without a `Duration` lives five minutes, and one with only a sliding window is limited by `MaxExpiration`.
- Getting started: use one version of every MethodCache package (NuGet warns with NU1608 when they differ), and pin
  `MethodCacheAnalysisLevel` only when warnings are errors. Every rule up to 1.0.0 counts as a rule of 1.0.
- Memory cache: `MethodCacheWeigher.Bytes(otherValue)` weighs the key on top of `otherValue`.
- Testing: `RegistrationProblems` runs the application's registration code, so infrastructure registered eagerly is
  still needed, and a recipe for testing that a cached method refuses another tenant's id
  ([A load for another tenant fails](docs/features/testing.md#a-load-for-another-tenant-fails)).

## 1.0.0-rc.12 (2026-11-07)

### Upgrading from 1.0.0-rc.11

- `CachedServices.ProblemsAsync` and `TagCoverage.Uninvalidated` throw `InvalidOperationException` when the provider
  has no service with cached methods, instead of returning an empty list. Pass the application's provider.
- New warning MC0038 fails a build that treats warnings as errors when `SlidingExpiration` on `[Cache]` or
  `[assembly: CacheInterface]` is not shorter than the `Duration` next to it. Such a window never extended anything;
  shorten it or remove it.

### Added

- `CachedServices.RegistrationProblems(provider)` in `MethodCache.Testing` checks that every service with cached
  methods is registered before `AddMethodCache`, without resolving any of them, for services that need a database or a
  key vault to resolve. The testing guide says what `ProblemsAsync` adds beyond the check a host makes when it starts
  ([Every cached service resolves](docs/features/testing.md#every-cached-service-resolves)).
- `MethodCacheWeigher.Bytes(otherValue: 1024)` weighs values it can't measure, such as records of your own, as the
  weight you give instead of 64 ([Bounding by size](docs/features/memory-cache.md#bounding-by-size)).
- `[Cache(InvalidatedManually = [...])]` marks tags that only an operator clears by hand, such as through
  `MapMethodCacheAdmin`, and that otherwise expire. MC0028 and `TagCoverage` count them as invalidated, so they need
  neither a `SuppressMessage` nor `alsoInvalidatedBy` ([Cleared by hand](docs/features/invalidation.md#cleared-by-hand)).
- Warning MC0038 for a `SlidingExpiration` that is not shorter than the `Duration` on the same attribute, and a warning
  in the log when the host starts for each method whose sliding window and duration, from any place such as a method's
  `WithSlidingExpiration` and `Defaults`, combine that way
  ([Sliding expiration](docs/features/expiration.md#sliding-expiration)).

### Documentation

- Getting started: a recommended project setup (`MethodCacheAnalysisLevel` in `Directory.Build.props`, the
  `MethodCache.Testing` checks, and evictions and hit ratio to watch).
- Monitoring: the prometheus-net example filters to MethodCache's meter, the hit-ratio query for prometheus-net's
  series names, `label_replace` and relabeling for a short method name, and why an adapter started from a hosted
  service exports nothing in some test hosts.
- Expiration: what `DurationFor` gets for a null result, and how to keep "nothing yet" until a known time.
- MC0026: suppress it on the class for cached reads of a table that the request doesn't filter.
- Distributed cache: the worker and API recipe links to the PostgreSQL tables and grants, and says the API's test
  project references the worker for `TagCoverage`.

## 1.0.0-rc.11 (2026-11-07)

### Upgrading from 1.0.0-rc.10

- `TagCoverage.Uninvalidated` counts a tag marked `InvalidatedElsewhere` only when a project the test project references
  invalidates it. Reference the project that invalidates it, such as the worker, from the test project, or name a tag
  that is invalidated outside the solution in `alsoInvalidatedBy`. `UninvalidatedTag` has a third parameter,
  `MarkedInvalidatedElsewhere`.
- MC0026 also follows an interface to its implementations in referenced projects, once those projects are built with
  this version, so it can report a cached implementation it didn't report before.
- New warning MC0036 fails a build that treats warnings as errors when a name in `methodcache.request_state_types`
  matches no type.
- With a distributed cache and its backplane, runtime overrides apply on every instance and outlive restarts. Such an
  override can't set `CacheWhen`, `DurationFor`, `TagsFor` or `SlidingFor` (`ArgumentException`); set them in
  `AddMethodCache`. `UpsertAsync`, `RemoveAsync` and `ClearAsync` throw an `InvalidOperationException` when the
  distributed cache doesn't store the change, and `GetOverrides()` lists the overrides of every instance. During a
  rolling upgrade, instances on rc.10 ignore stored overrides.

### Added

- `[ChecksRequestState]` marks a method of your own that checks a parameter against the current request, such as
  `EnsureActsFor(tenantId)`. MC0026 doesn't report a class whose cached methods each pass one of their parameters to
  it ([A tenant parameter checked against the request](docs/user-guide/DIAGNOSTICS.md#a-tenant-parameter-checked-against-the-request)).
- MC0026 follows an interface to the classes that implement it in referenced projects. The generator writes the
  classes of each project that take per-request state into its assembly (`MethodCacheRequestState.g.cs`).
- The generator writes the tags each project invalidates into its assembly (`MethodCacheInvalidatedTags.g.cs`), and
  `TagCoverage` reads them from the projects next to the tests.
- Warning MC0036: a name in `methodcache.request_state_types` that matches no type, such as one left behind by a
  rename.
- Information MC0037: a `[Cache]` method whose key is written when the call runs, such as one with an `object`
  argument. It shows in the editor and never stops a build.
- The MSBuild property `MethodCacheAnalysisLevel` turns off the analyzer rules that come in versions after the one it
  names, so a new version can't break a build that treats warnings as errors
  ([Rules of a later version](docs/user-guide/DIAGNOSTICS.md#rules-of-a-later-version)).
- The `MethodCache` package carries this `CHANGELOG.md` in its `docs` folder, next to the docs of that version.
- New docs: giving a class an interface ([Getting started](docs/getting-started.md#a-class-without-an-interface)),
  replacing a cached service with a fake in a test, generic EF Core helpers with `[InvalidatesCache]`, a worker and
  an API with PostgreSQL in "invalidations only" mode, prometheus-net with a registry of your own, `DurationFor` for
  future-dated rows, what changed keys mean during a rolling deploy, and where MC0027 and MC0030 read what they know
  about referenced projects.
- `SlidingFor` sets the sliding window from the result, like `DurationFor` sets the duration: a long window for a
  finished value and none for one still being processed
  ([A sliding window from the result](docs/features/expiration.md#a-sliding-window-from-the-result)). Your own
  `ICacheManager` reads it with `CacheEntryOptions.SlidingExpirationOf(value)`. MC0033 checks its type like
  `DurationFor`'s.
- The counter `methodcache.evictions` counts the entries each instance removed to make room since it started, per
  `methodcache.kind`. The monitoring guide shows the hit ratio per method in PromQL
  ([Hit ratio per method](docs/features/monitoring.md#hit-ratio-per-method)).
- `MethodCacheWeigher.Bytes`, a ready-made weigher that counts approximate bytes of strings and arrays of numbers:
  `o.Weigher = MethodCacheWeigher.Bytes`.
- `CachedServices.ProblemsAsync(provider)` in `MethodCache.Testing` checks that every service with cached methods resolves
  to its caching decorator, so a registration mistake fails a test
  ([Every cached service resolves](docs/features/testing.md#every-cached-service-resolves)).
- `MapMethodCacheAdmin(..., audit: ...)` records who changes the cache through the admin endpoints: the user, the
  action, its targets and the time ([Audit](src/MethodCache.AspNetCore/README.md#audit)).
- Runtime overrides apply on every instance with Redis, SQL Server or PostgreSQL and their backplane: they are stored in
  the distributed cache, the other instances apply them within moments, and an instance that starts applies the active
  ones, so they survive restarts and slot swaps ([Runtime overrides](docs/features/configuration.md#runtime-overrides)).

### Changed

- The agent instructions ask a coding agent to show its feedback draft to its user and to file it only when the user
  agrees.
- The error for a cached service registered after `AddMethodCache` says how a test that replaces a service with a
  fake avoids it: call `AddMethodCache` again after the fake.
- MC0031 suggests a private method that does the work when the call doesn't need the cache, such as a recursive call.
- The MC0026 message points to `[ChecksRequestState]` for a tenant or user that the method takes as a parameter.
- A policy in code or appsettings for a cached service that the application doesn't register is logged at
  `Information` instead of `Debug`, so a misspelt or moved service shows in the startup log.

### Fixed

- **`MethodCache.EntityFrameworkCore`**: a many-to-many link added or removed with only the two ids, without loading
  either side, invalidates every mapped tag of both sides. The side the context hasn't loaded is read once before the
  save when its tags use more than its key. Before, only tags that use the key alone, such as `product:{Id}`, were
  invalidated, and a cached list by category kept the old links.
- **`MethodCache.EntityFrameworkCore`**: a row changed without loading it invalidates the new value of a column the
  database computes, such as `HasComputedColumnSql`. Before, only the old value was invalidated.
- **`MethodCache.EntityFrameworkCore`**: the warning for an invalidation that can't be confirmed after a commit gives
  the number of tags and the error's type. Before, it logged the tags, which can hold user or tenant ids, and the
  exception with its message.
- **`MethodCache.EntityFrameworkCore`**: `UseEntityFrameworkCore` works in a container without logging, such as a
  `ServiceCollection` built without a generic host. Before, it threw because no `ILogger<T>` was registered.

## 1.0.0-rc.10 (2026-11-06)

### Upgrading from 1.0.0-rc.9

- Two new build errors find what failed when the call ran: MC0033 for `CacheWhen<T>`, `DurationFor<T>` or `TagsFor<T>`
  of a type the result can never be, and MC0034 for a key argument whose state is all private.
- `AddMethodCache` throws when a registered interface has `[Cache]` methods but the source generator didn't run in its
  project, where its calls ran uncached before.
- New warnings MC0030, MC0031, MC0032 and MC0035 fail a build that treats warnings as errors until they are fixed or
  suppressed.
- Entries with a set or dictionary argument get a new key, so they are missed once after the upgrade.

### Added

- A way for coding agents to report problems: the agent instructions (`docs/ai/methodcache.md`, Feedback) say when
  and how to file an `[Agent feedback]` issue, without the project's code, names or secrets, and the repository has an
  issue form with the same fields.
- `ICacheManager.SetAsync(key, value, options)` stores a value you already have, such as one you just saved, in memory
  and the distributed cache. Other instances drop their copy, and a load already running for the key doesn't overwrite
  it. A value the options turn down removes the key's old value instead.
- `TagsFor` adds tags read from each result to its entry, next to the method's own tags, such as the customer of an
  order found by its id: `mc.Method(...).TagsFor(order => [MethodCacheKeys.Tag($"customer:{order.CustomerId}")])`, and
  `TagsFor<T>` on `CacheEntryOptions.Builder` for manual caching. Custom managers read them with
  `CacheEntryOptions.TagsOf(value)`.
- The cache logs one `Information` line when it starts, naming the memory cache's `MaxItems`, and the distributed
  cache's store, `KeyPrefix` and backplane, or that there is no distributed cache, so configuration that differs
  between instances shows in the log.
- Build error MC0034 reports a key argument of a `[Cache]` method, or a value inside one, whose type has only private
  state, which MethodCache refuses when the call runs since every value would get the same key.
- Analyzer MC0035 warns when the factory of `GetOrCreateAsync` or `GetOrCreateManyAsync` uses a cancellation token
  from outside it, such as `HttpContext.RequestAborted`, that the call doesn't pass as its `cancellationToken`: its
  cancellation fails the load for every caller sharing it. The code fix makes the factory use its own token and passes
  the outer one as `cancellationToken`.
- `MethodCacheKeys.Part` and `MethodCacheKeys.Tag($"...")` take nullable values (`int?`, `Guid?`, a nullable enum and
  the other value types they take), writing the value or null the way tag templates do.
- `AddMethodCache` fails with an `InvalidOperationException` when a registered interface has `[Cache]` methods but
  MethodCache's source generator didn't run in its project, so its calls would run uncached. The message names the
  project and what to check.
- Analyzer MC0030 reports a program with cached services, its own or a referenced project's, when its code calls
  neither `AddMethodCache` nor a method of a referenced project that calls it, such as a shared
  `services.AddData()`. For this, MethodCache marks a project's assembly with `AssemblyMetadata` attributes when it
  has cached services, and for each of its methods that calls `AddMethodCache`, also through other setup methods.
- Analyzer MC0031 reports a call a class makes to its own cached method, which skips the cache.
- `DurationFor` gives each result its own duration in place of `Duration`, such as the time left on a token or a
  shorter time for an empty result: `mc.Method(...).DurationFor(token => token.ExpiresAt - DateTimeOffset.UtcNow)`, and
  `DurationFor<T>` on `CacheEntryOptions.Builder` for manual caching. Zero or less doesn't cache the result. Custom
  managers read it with `CacheEntryOptions.DurationOf(value)`.
- `MethodCacheKeys.Tag($"price:{provider}:{model}")` builds a tag from an interpolated string, writing each value the
  way `[Cache]` tag templates do. A value of a type tags can't hold, or with a format, doesn't compile. MC0024's code
  fix offers it, and MC0028 counts tags built with it.
- MC0026 reads the application's own request-state types, such as an `ICurrentUser` service, from the `.editorconfig`
  option `methodcache.request_state_types`.
- `GetOrCreateManyAsync<TId, T>(ids, id => $"user:{id}", (missingIds, ct) => ..., options)` takes ids and how each
  makes its key; the factory gets the missing ids and returns a dictionary by id, and so does the call.
- Analyzer MC0032 reports a class that implements a cached interface created with `new` outside a service
  registration, since its calls skip the cache. The docs show keyed registrations for several instances of one class.
- Analyzer MC0033 is a build error for `CacheWhen<T>`, `DurationFor<T>` or `TagsFor<T>` in `Method(...).Configure(...)`,
  `GetOrCreateAsync`, `GetOrCreateManyAsync` or `SetAsync` for a `T` the call's result can never be.
- The memory cache logs one warning the first time it is full and removes an entry to make room, naming the limit
  (`MaxItems`, or `WeightLimit` with a weigher) to raise when entries are removed before they expire.

### Changed

- A setting for a `[Cache]` method whose service the application doesn't register, in `mc.Method(...)` or in
  `appsettings.json`, is skipped instead of failing startup, so startup code and configuration shared by several
  applications works in each of them. A method id that matches no `[Cache]` method still fails.
- MC0028 counts a `[CacheInvalidate]` declared on an interface in a referenced project, such as a shared contracts
  project, so a tag that the referenced interface invalidates is no longer reported.
- MC0026 follows constructors two levels down instead of one, and follows an interface to the classes in the project
  that implement it, so a cached service that takes `IUserRepository`, whose implementation takes a `DbContext` that
  takes `IHttpContextAccessor`, is reported.
- Sets and dictionaries in arguments are written sorted in keys, so two equal sets or dictionaries get one key whatever
  order they enumerate in, also on two servers. Existing entries with a set or dictionary argument are missed once.
- The error for a `CacheWhen<T>` or `DurationFor<T>` of another result type names the types as C# writes them,
  `CacheWhen<long?> can't check the int result`, instead of ``Nullable`1`` and `Int32`. `NoOpCacheManager` makes the same
  check, so a test with it finds the mistake too.
- `GetOrCreateManyAsync` accepts a factory answer that leaves out keys, such as ids that don't exist, instead of
  failing the whole call. A key left out is not cached and not returned, unless it has a fail-safe value, which it
  then gets as when the factory throws. A `null` answer still fails.

## 1.0.0-rc.9 (2026-11-05)

### Changed

- MC0027 in a project that references the one with the `ef.Entity<T>(...)` mapping, such as its tests or a worker,
  checks writes against that mapping, so a write to a table that nothing caches is no longer reported there. For this,
  MethodCache writes a project's mapping into its assembly as `AssemblyMetadata` attributes.
- MC0027 reads constants and numbers interpolated into `ExecuteSqlRaw`, so
  `$"SELECT pg_advisory_xact_lock({LockId})"` and `$"SET LOCAL lock_timeout = '{waitMs}ms'"` are no longer reported.
  An interpolated string still is, since it could hold any SQL.

## 1.0.0-rc.8 (2026-11-05)

### Added

- `[Cache(InvalidatedElsewhere = ["catalog"])]` marks the tags that another application or process invalidates, such as
  an import worker. Analyzer MC0028 and `TagCoverage` count them as invalidated, on every method that has the tag, so
  neither a `#pragma` nor a list in the test is needed. Caching itself doesn't change. Analyzer MC0029 reports a tag
  in `InvalidatedElsewhere` that matches none of the method's `Tags`.

### Changed

- `TagCoverage.Uninvalidated` counts the entity mapping of `mc.UseEntityFrameworkCore` by itself, so
  `alsoInvalidatedBy: mapping.AllTags` is no longer needed. `alsoInvalidatedBy` remains for tags the code invalidates
  with `InvalidateTagAsync`.

## 1.0.0-rc.7 (2026-11-05)

### Breaking changes since 1.0.0-rc.6

- A duration is a number and a unit: `"30s"`, `"10m"`, `"1.5h"`, `"7d"`. A `TimeSpan` such as `"00:10:00"` is no longer
  accepted: in an attribute it is error MC0004, whose message and code fix give the value with a unit (`"10m"`), and
  in `appsettings.json` startup fails with the value to write instead. One way to write a duration keeps an
  application consistent. `TimeSpan` arguments in code are unchanged.

### Changed

- MC0027 reports raw SQL only when it names the table of a mapped entity type (by class, `DbSet`, `ToTable` or
  `[Table]` name, in any case, so `ai_model_prices` matches `AiModelPrice`), runs a procedure or block (`call`,
  `exec`, `do`), or can't be read. Statements that only `select` or `set` a session setting, such as
  `SET LOCAL lock_timeout`, are never reported. A project that maps no type still reports all other raw SQL.
- MC0026 also reports a cached implementation that takes a class whose constructor takes per-request state, such as a
  `DbContext` that takes `IHttpContextAccessor`, directly or through `IDbContextFactory<T>`.

### Added

- Analyzer MC0028 reports a tag on a `[Cache]` method that nothing in the project invalidates: no `[CacheInvalidate]`,
  entity mapping, `InvalidateTagAsync`, `InvalidateAfterCommit` or `[InvalidatesCache]` call names it. A placeholder
  matches any value. A project that invalidates nothing, such as a library of cached interfaces, isn't reported; a tag
  another project invalidates is suppressed with a `#pragma`.
- The `MethodCache.Testing` package, for tests of the caching itself. `services.AddMethodCacheTesting()` registers a
  `FakeTimeProvider` as the container's clock and a `CacheRecorder`: `Loads<T>(call)` counts how many times a call ran
  your implementation, and `InvalidatedTags` and `InvalidatedKeys` list what was invalidated.
  `TagCoverage.Uninvalidated(provider, alsoInvalidatedBy)` lists the tags of cached methods, also from configuration,
  that no `[CacheInvalidate]` method or listed tag covers, across the whole application.
- `EntityTagMapping<TContext>` in `MethodCache.EntityFrameworkCore` lists the mapped entity types with their tags, and
  `AllTags` every tag of the mapping, such as for `TagCoverage`.

### Documentation

- Entity Framework Core: a row-level security section, with a context of their own for cached reads, and what happens
  when a save of a row the context didn't load can't read it (PostgreSQL and SQL Server don't change the row either,
  and the save throws `DbUpdateConcurrencyException`). EF Core query filters don't hide the row from that read.
- The PostgreSQL, Redis and SQL Server package READMEs show how to share only invalidations, such as for a worker that
  changes data an API caches. The agent guide lists every feature guide and says when to read it.
- Testing: how to prove a read is cached when every save invalidates.
- Monitoring: prometheus-net 8 exports MethodCache's instruments with no setup, and how its names and types differ
  from OpenTelemetry's Prometheus exporter.
- Examples use durations with a unit throughout.

## 1.0.0-rc.6 (2026-11-04)

### Changed

- Analyzer MC0027 reports `ExecuteUpdate` and `ExecuteDelete` only on the entity types the project maps with
  `ef.Entity<T>(...)`, and on their base and derived types, so writes to tables that nothing caches are no longer
  reported. A project that maps no type, such as a data layer apart from the startup project, is still reported on
  every type. Raw SQL that is one statement starting with `select`, such as an advisory lock, is no longer reported.

### Added

- `ef.Entity<User>(user => user.WhenDeleted("posts"))` maps tags that are invalidated only when a row is deleted, with
  the deleted row's values in the braces. Use it for rows the database deletes with the entity, through
  `ON DELETE CASCADE` or a trigger, when the entity is saved too often to invalidate them on every change.
  `user.Tags(...)` in the same call maps tags for any change. A type mapped only with `WhenDeleted` is reported by
  MC0027 for `ExecuteDelete` alone.
- `[InvalidatesCache]` marks a method of your own that invalidates, such as a helper around
  `ICacheManager.InvalidateTagsAsync`. MC0027 counts a call to it as the invalidation and doesn't report the writes
  inside it.

## 1.0.0-rc.5 (2026-11-04)

### Breaking changes since 1.0.0-rc.4

- `ICacheManager.TryGetAsync` and `ICacheManager.InvalidateKeysAsync` take every argument: `(key, version,
  cancellationToken)`. Extension methods give the short forms, so a cancellation token no longer needs a named
  argument: `TryGetAsync<T>(key, cancellationToken)`, `InvalidateKeyAsync(key, cancellationToken)` and
  `InvalidateKeysAsync(keys, cancellationToken)`, and with a version `TryGetAsync<T>(key, 2, cancellationToken)`,
  `InvalidateKeyAsync(key, 2, cancellationToken)`. Calls that passed `version: null` drop it; your own
  `ICacheManager` keeps working.

### Added

- `IMethodCacheKeys.Tag<T>(call, template)` returns the tag a call to a cached method stores its result under, filled in
  exactly as the decorator fills it in. Use it to invalidate when the data changes outside the cached interface, such
  as in an endpoint that writes to the database directly:
  `cache.InvalidateTagAsync(keys.Tag<IPrices>(s => s.GetRateAsync(provider, model), "price:{provider}:{model}"))`.
  The template must be one of the method's tags.
- Analyzer MC0024 warns when a tag built with string interpolation for `InvalidateTagAsync` or `InvalidateTagsAsync`
  writes a value differently from MethodCache's tags, such as a string with `:` (`gemma3:4b`), a `Guid`, an enum or a
  `bool`. Such an invalidation matched nothing, without an error. The code fix wraps the value in
  `MethodCacheKeys.Part`.
- The XML documentation says when `[CacheInvalidate]` runs, how tag arguments are written, what `Weigher` receives,
  how to skip caching null for one method, and how to clear the cache after a test writes to the database directly.

- Analyzer MC0025 warns when a cached method returns a type that isn't safe to share between callers: `JsonNode`
  (`JsonObject`, `JsonArray`), `DataTable`, `DataSet`, `DataRow`, `DataView`, a `Stream` or a `StringBuilder`, also as
  an element. Every caller gets the same instance at once, and a cached `JsonObject` read by two requests at the same
  time could come back empty. A `Stream` result reported MC0020 before.
- Analyzer MC0026 warns when the implementation of a cached interface takes `IHttpContextAccessor`, `HttpContext`,
  `HttpRequest` or a `ClaimsPrincipal` in its constructor: the key holds only the method's arguments, so a result
  that depends on the current user could reach another user.
- **`MethodCache.EntityFrameworkCore`**, a new package: `AddMethodCache(mc => mc.UseEntityFrameworkCore<AppDb>(ef =>
  ef.Entity<Product>("product:{Id}", "products")))` invalidates those tags after every commit that adds, changes or
  deletes a `Product`, with the old and the new values of a changed row, and drops them on a rollback. A row saved
  without loading it (`Update`, `Attach`, `Remove(new Product { Id = id })`) is read once for its stored values.
  `OwnsOne` and `OwnsMany` types invalidate their owner, many-to-many links both sides, and cascade deletes and other
  interceptors' changes count. `db.InvalidateAfterCommit(tags)` queues other tags for the same moment. Works with
  `AddDbContext`, `AddDbContextPool`, `AddDbContextFactory`, `TransactionScope` and `UseTransaction`, and with document
  databases on `SaveChanges`.
- Analyzer MC0027 warns, in a project that uses `MethodCache.EntityFrameworkCore`, about `ExecuteUpdate`,
  `ExecuteDelete` and `ExecuteSql…` writes, which go past EF Core's change tracker, when the method around them
  doesn't invalidate. MC0024 also checks the tags passed to `InvalidateAfterCommit`.

### Documentation

- How to keep values in each process's memory and share only invalidations
  ([Invalidations only](docs/features/distributed-cache.md#invalidations-only)), how to invalidate after a database
  transaction, per-user data and row-level security in Getting started, and a table of duration units.

- The `MethodCache` package carries the user documentation in its `docs` folder, with `llms-full.txt`, so a coding
  agent can read the docs for the installed version. The README has a prompt to give your agent.
- New [How do I…?](docs/how-to.md) page with short answers to common questions, and reference pages with the exact
  rules for [registration](docs/reference/registration.md), the [distributed cache](docs/reference/distributed.md) and
  [HTTP caching](docs/reference/http-caching.md).
- The README, the NuGet READMEs and the guides were rewritten to say what you do and see, without implementation
  detail. New sections: why an interface is needed, invalidating when the write happens elsewhere, and testing with
  writes straight to the database.
- `llms.txt` now lists PostgreSQL and the new pages.

## 1.0.0-rc.4 (2026-11-04)

### Breaking changes since 1.0.0-rc.3

A third API review before 1.0.0 found settings that were accepted and then silently did something else. They now
fail where they are set. No API is removed.

- `FromAssemblyOf<T>()` and `FromAssembly(...)` choose the module that wins when several assemblies generate the same
  interface; the services the other modules generate stay cached. Before, they turned caching off for every interface
  outside the selected assemblies, without a warning. With `AutoRegister`, only the selected modules register missing
  services, as before.
- A `Duration` longer than `MethodCacheMemoryOptions.MaxExpiration` (7 days by default) fails where it is set: at
  startup from `[Cache]`, code or `appsettings.json` (a reload with one is rejected and the previous policies stay),
  with an `ArgumentException` from a runtime override, and with an `ArgumentOutOfRangeException` from a manual
  `GetOrCreateAsync` or `GetOrCreateManyAsync` before the factory runs. Before, the entry was removed after
  `MaxExpiration` instead. Shorten the `Duration`, or raise the limit with `mc.Memory(o => o.MaxExpiration = ...)`.
  With your own `ICacheManager` nothing changes.
- A `CacheWhen<T>` for a type the method can't return, such as `CacheWhen<string>` on a method that returns `int`,
  fails where it is set: at startup from code, and with an `ArgumentException` from
  `IRuntimeCacheConfigurator.UpsertAsync`, which leaves the policy as it was. Before, the override was accepted and
  every call of the method threw. A generic method's result type depends on the call, so there the call still throws.
- `IMethodCacheKeys.For` throws an `InvalidOperationException` when the interface has several registrations and the
  last one is a scoped or transient factory, which may create another type in each scope. Before, it returned the key
  of the type the factory created in a scope of its own, which could miss the caller's entry. Register the service with
  its implementation type, give it a service key of its own, or invalidate by tag.

### Added

- **`MethodCache.PostgreSql`**: `PostgreSqlOptions.ConfigureDataSource` configures MethodCache's
  `NpgsqlDataSourceBuilder`s, for passwordless login with a token from Microsoft Entra ID or AWS IAM, client
  certificates or a custom server certificate check. It applies to the connection pool and to the connection that
  listens for invalidations.

### Fixed

- **`MethodCache`**: `GetOrCreateManyAsync` checks the factory's answer before it caches any of it. An answer that
  leaves out a key, or is `null`, fails like a factory that throws: keys with a fail-safe value get it, and nothing
  from the answer is cached. Before, the values for the other keys were cached before the call threw.
- **`MethodCache`**: `IMethodCacheKeys.MethodId` no longer creates the service; it reads the method from the call.

### Documentation

- [Invalidation](docs/features/invalidation.md#with-several-instances) says what each count of `InvalidationResult`
  confirms, and [What other instances see](docs/features/distributed-cache.md#what-other-instances-see) how Redis,
  SQL Server and PostgreSQL catch up after a lost backplane connection.

Code that earlier release candidates generated keeps working. It doesn't name the methods' result types, so for it a
`CacheWhen<T>` of the wrong type is still reported by the call.

## 1.0.0-rc.3 (2026-11-04)

The packages are on nuget.org only. GitHub Packages no longer has the MethodCache packages, so remove a
`nuget.pkg.github.com/eoniclabs` source you added for them.

### Added

- **`MethodCache.PostgreSql`**: PostgreSQL as the distributed cache, `AddMethodCache(mc => mc.UsePostgreSql(connectionString))`.
  Invalidations reach other instances at once through `LISTEN`/`NOTIFY`, with the invalidations table as the truth
  and a poll every second as a safety net, so a lost notification or a pooler in transaction mode only delays them.
  Every read and every new value is one prepared statement; new cache values and backplane messages are committed
  without waiting for the disk (replacements of live values and invalidation stamps are not), and values up to about
  8 KB stay in the row. Small values read about as fast as from Redis. After a crash, a failover or a restore, every
  instance clears its in-memory cache. The tables are created at startup, or by the package's
  `migrations/0001-create-schema.sql`. Requires PostgreSQL 13 or later.

### Fixed

- **`MethodCache.SqlServer`**: cache commands no longer join the application's `TransactionScope`. A scope that rolled
  back undid the cache writes and invalidations made inside it, and the cache's connection could turn it into a
  distributed transaction.
- **`MethodCache.SqlServer`**: an instance clears its in-memory cache when it may have missed invalidations in two more
  cases: SQL Server was unreachable from startup for half of `BackplaneRetention`, or the instance fell behind the
  invalidations table for that long. Instances that share a `KeyPrefix` now keep invalidations as long as the longest
  `BackplaneRetention` any of them uses, so one with a shorter setting no longer deletes rows the others haven't read,
  and old invalidations are deleted apart from polling, so a large delete no longer holds back delivery.
- **`MethodCache`**: disposing the cache no longer deadlocks with a custom `IBackplane` whose `UnsubscribeAsync` waits
  for a delivery in progress.

## 1.0.0-rc.2 (2026-10-20)

### Breaking changes since 1.0.0-rc.1

Two API reviews before 1.0.0 removed duplicates and names that didn't match the rest of the API, and made settings
that were accepted but did nothing fail at startup instead. Each change below says what to write instead.

Registration
- `AddMethodCacheRedis`, `AddMethodCacheSqlServer` and `AddMethodCacheMessagePack` are removed. Use the builder:
  `services.AddMethodCache(mc => mc.UseRedis("localhost:6379"))`, `mc.UseSqlServer(...)` and `mc.UseMessagePack()`.
- The classes that hold these methods are now `RedisMethodCacheBuilderExtensions`,
  `SqlServerMethodCacheBuilderExtensions` and `MessagePackMethodCacheBuilderExtensions`, and `MethodCacheAdminEndpoints`
  is `MethodCacheEndpointRouteBuilderExtensions`. This matters only where you call them as static methods.
- An app has one distributed cache: `UseRedis` and `UseSqlServer` together throw in `AddMethodCache`, naming both.
  Before, the app started, stored values in the second one and kept the first one's backplane. Keep one of them.
- `mc.Method(...)` on a method without `[Cache]` fails at startup. Before, it logged a warning and changed nothing. Add
  `[Cache]` to the method, or remove the line.
- The `MethodCacheBuilder.Method` and `IRuntimeCacheConfigurator` overloads for methods that return nothing
  (`Expression<Action<TService>>` and `Expression<Func<TService, Task>>`) and `IFluentMethodConfiguration` without a
  type argument are removed, since such methods can't be cached. Pointing `Method`, `UpsertAsync` or `RemoveAsync` at
  a `void`, `Task` or `ValueTask` method throws an `ArgumentException`.
- Several registrations of one interface under the same service key can no longer share cached results when they may
  be configured differently: an instance registration whose type another of them also has fails in `AddMethodCache`,
  and a factory that creates the same type as another of them fails once both are created. Register them under
  different service keys.
  Registrations the container builds from the same type, such as a library and the app both calling
  `AddSingleton<IFoo, Foo>()`, still share their results.

Options and attributes
- `CacheEntryOptions.Builder.RefreshAhead` is now `WithRefreshAhead`, like every other builder method.
- `CacheEntryOptions.Builder.FailSafe(duration, timeout, retryAfter)` is now three methods, one per setting as in
  `[Cache]` and `appsettings.json`: `WithFailSafe(duration)`, `WithFailSafeTimeout(timeout)` and
  `WithFailSafeRetryAfter(retryAfter)`, so an override can change one value without repeating the others.
- `[Cache(RetryAfter = ...)]` and `[CacheInterface(RetryAfter = ...)]` are now `FailSafeRetryAfter`, the name used in
  code and in `appsettings.json`.
- `CacheEntryOptions.Builder.WithTags` is now `AddTags`, since it adds to the tags instead of replacing them. It
  throws an `ArgumentException` for a null, empty or whitespace tag, as `InvalidateTagsAsync` does; before, such a
  tag was skipped.
- `IFluentMethodConfiguration<TResult>.WithVersion(n)` is removed; write `.Configure(o => o.WithVersion(n))`.
- `CacheWhen` is set per method: in `mc.Defaults(...)` or `mc.Group(...)` it fails at startup. Before, every method
  that didn't return that type threw an `InvalidOperationException` on each miss, after the method had run. Use
  `mc.Method(...).CacheWhen(...)` for each method it applies to.
- A `CacheWhen<T>` for a type the method can't return, such as `CacheWhen<string>` on an `int` method, throws an
  `InvalidOperationException` on the call. Before, it silently cached nothing.
- `[Cache]` or `[assembly: CacheInterface]` with a `RefreshAhead` that isn't shorter than the `Duration` or
  `SlidingExpiration` next to it is the new error MC0023, as in code and `appsettings.json`. Before, it compiled and the
  window was cut to half the entry's life while the app ran. Make it shorter than both, or remove it.
- `MethodCacheMemoryOptions.SizeLimit` is now `WeightLimit`. `MaxItems` and `WeightLimit` both bound the cache, so an
  entry is evicted when either is reached; before, a `Weigher` made `MaxItems` stop counting. Setting `WeightLimit`
  without a `Weigher` fails at startup instead of being ignored.
- `MethodCacheMemoryOptions`, `RedisOptions` and `SqlServerOptions` are sealed, like the other options classes.

Configuration files
- The `"Services"` section of `appsettings.json` is now `"Methods"`, since each key in it is a method id; environment
  variables are named `MethodCache__Methods__0__MethodId` and so on. A leftover `"Services"` section fails at startup
  as an unknown setting.
- Startup fails, naming the setting, for configuration that was ignored or applied in a way nobody asked for:
  - A method id that matches no cached method, such as a misspelled name or an id without its `[Assembly]` prefix.
    The message suggests the closest cached id. Before, a warning was logged.
  - A method listed twice, for example by name in `appsettings.json` and under an index in an environment variable.
    Before, one of them replaced the other without a word. To override a file from another source, list the method
    under the same index in both.
  - `"*"` or `"group:<name>"` as a method id. Use `Defaults`, or the new `Groups` section.
  - `Group`, `Metadata` or `Notes` in `Defaults`, and a `"group"` key in a method's `Metadata`, which moved the method
    to that group. Use `Group` on the method.
  - The same fail-safe value both in `Defaults` and in the top-level `FailSafe` block, such as
    `Defaults.FailSafeTimeout` and `FailSafe.Timeout`. Set it in one place.
- Tag templates such as `"order:{orderId}"` from `appsettings.json`, from startup code and from runtime overrides are
  checked against the parameters of every method they reach, with the rules of MC0010, MC0016 and MC0017 for
  `[Cache]`. A template with a misspelled parameter fails at startup, rejects a reload, or makes `UpsertAsync` throw.
  Before, it became a tag with the literal text, which no invalidation matched.

Keys
- `MethodCacheKeys.For`, `ForVersion`, `KeyedMethodName`, `TypePart`, `Sequence`, `Prefix` and `Pattern`, and the
  constants `MethodCacheKeys.Null` and `Separator`, are removed. A key built by hand went wrong without notice after a
  version, an overload, a generic argument or a keyed registration was added, and the prefix of `GetUser` also
  matched `GetUsers`. For one key, write `keys.For<IUserService>(users => users.GetUser(42))` with the new
  `IMethodCacheKeys`. For all entries of a method, pass `keys.MethodId<IUserService>(users => users.GetUser(0))` to
  `ICacheAdministrator.InvalidateMethodAsync` or `ListEntries(methodId: ...)`. `MethodCacheKeys.Part` stays, for
  tags.
- `CacheEntryOptions.GetCacheKey` and `CacheManagerExtensions.GetCacheKey` are replaced by
  `MethodCacheKeys.StoredKey(key, version)`. A generated call passes `Version = null` to your `ICacheManager`, since
  its key already holds the version. A custom manager stores under `StoredKey(key, options?.Version)`, and under
  `StoredKey(key, version)` in `TryGetAsync` and `InvalidateKeysAsync`. Before, a manager that added `options.Version`
  stored generated entries with the version twice, and invalidating the key from `IMethodCacheKeys` missed them.

Manual caching
- `ICacheManager.GetOrCreateAsync` takes a state and a factory `(state, ct) => ...`, like `HybridCache`, so a hot path
  can use a static lambda without a closure. `CacheContext` is removed: the factory gets the cancellation token, and
  `GetOrCreateAsync(key, ct => ...)` keeps working through the extension methods.
- `GetOrCreateManyAsync` factories take `(keys, ct)` instead of `(keys, context, ct)` and return an
  `IReadOnlyDictionary<string, T>` instead of an `IDictionary<string, T>`, and so does the call. A `Dictionary` works
  as both.
- The `TryGetAsync` extension method that duplicated `ICacheManager.TryGetAsync` is removed.
- `NoOpCacheManager` moved from `MethodCache.Extensibility` to `MethodCache` and is sealed; wrap it to change one call.

Administration
- `ICacheAdministrator.InvalidateTagsAsync` takes an `IEnumerable<string>` and returns the `InvalidationResult` in a
  `ValueTask`. It covers method and HTTP entries.
- `ICacheAdministrator.GetStatsAsync` returns a `CacheStats` record instead of an `ICacheStats`, which is removed, as
  is the AspNetCore `CacheStatsResponse`; the stats endpoint returns `CacheStats` unchanged.
- `CacheEntryInfo.Key` is now `StoredKey`, and `ListEntries`, `GetEntry` and `RemoveEntryAsync` take `storedKeyPrefix`
  and `storedKey`. It is the key as stored, which for a manual entry with a version is not the key you pass to
  `ICacheManager`. The admin endpoints return it as `storedKey`.
- `RemoveEntryAsync`, `InvalidateTagsAsync` and `ClearAsync` throw the public `CacheInvalidationQueuedException` when
  the change is applied in memory but still queued for the distributed cache. Before, they threw an internal type.
- `InvalidationQueues.Distributed` and `Publication` are now `Queued` and `PublicationQueued`, the names
  `InvalidationResult` uses. `Distributed` counted invalidations still waiting for the distributed cache, while
  `InvalidationResult.Distributed` counts confirmed ones.
- `CachedMethodInfo.Duration`, `Tags`, `Version`, `Sources` and `L2Namespace` are removed. Read `Options.Duration`,
  `Options.Tags` and `Options.Version`, `SettingSources` and `IsDistributedNamespaceShared` instead.
- `CachedMethodInfo.SettingSources` maps a `CacheEntryOptions` property name, such as `Duration` or
  `FailSafeTimeout`, to a `CacheSettingSource`: `Attribute`, `Code`, `Configuration` or `RuntimeOverride`. Before, its
  keys and values were internal names such as `"startup-fluent"`. The methods endpoint returns the same names.
- `CacheEntryInfo`, `CachedMethodInfo`, `InvalidationQueues`, `RuntimeOverride` and `RuntimeOverrideMetadata` use
  `init` properties instead of positional constructors, so new properties can be added in 1.x without breaking you.
  Write `new RuntimeOverrideMetadata { Owner = "ops", ExpiresAt = ... }`. `CacheEntryInfo.Format` is removed.
- If you implement `ICacheAdministrator` yourself: `GetInvalidationQueues()` has no default body any more (in rc.1 it
  returned `new(0, 0)`), and `InvalidateMethodAsync` and the `methodId` parameter of `ListEntries` are new.

Runtime overrides
- `IRuntimeCacheConfigurator` has the shape of `ICacheAdministrator`: `UpsertAsync`, `RemoveAsync` and `ClearAsync`
  return a `ValueTask` instead of a `Task`, and `GetOverridesAsync()` is now `GetOverrides()`, which returns an
  `IReadOnlyList<RuntimeOverride>` ordered by method id. `await` works as before; call `.AsTask()` where you need a
  `Task`.
- The expression helpers take one type argument, like `IMethodCacheKeys.For`: write
  `UpsertAsync<IOrdersService>(s => s.GetAsync(Any<int>.Value), o => ...)` instead of
  `UpsertAsync<IOrdersService, Order>(...)`, and the same for `RemoveAsync`. They work for methods returning `T`,
  `Task<T>` and `ValueTask<T>`. A method without `[Cache]` throws an `ArgumentException` that names the closest cached
  method id.

ASP.NET Core
- `DELETE {prefix}/entries` is replaced by `POST {prefix}/clear` with the body `{}`. Before,
  `DELETE entries?tag=orders`, the natural guess after `GET entries?tag=orders`, ignored the tag and emptied the whole
  cache on every instance.
- The query parameters `key` and `prefix` are now `storedKey` and `storedKeyPrefix`, the names the JSON uses. A query
  parameter an endpoint doesn't take, or a body property it doesn't take (`invalidate` takes only `tags`), answers 400
  and changes nothing.
- `MethodCacheHealthCheck` and the request type of the invalidate endpoint are internal; register the check with
  `services.AddHealthChecks().AddMethodCache()` and call the endpoints as before.
- The health check reports the invalidations waiting for the distributed cache as `queued` instead of
  `invalidationQueued`.

Telemetry
- Metric and span attributes are in the `methodcache.` namespace: `kind`, `name`, `result`, `type`, `stage` and
  `operation` are now `methodcache.kind`, `methodcache.method` (`methodcache.http.client` for an `HttpClient`),
  `methodcache.result`, `methodcache.invalidation.type`, `methodcache.invalidation.stage` and `methodcache.operation`.
  Before, `name` and `kind` clashed with the application's attributes and with a span's own fields. Update dashboards,
  alerts and views.
- An `ICacheManager` call by key reports `methodcache.kind=manual` and no method, instead of
  `name="MethodCache.Fluent"` or `"MethodCache.Fluent.Bulk"`; a `GetOrCreateManyAsync` batch adds
  `methodcache.bulk=true`.
- `methodcache.load.duration` and `methodcache.distributed.duration` advise bucket boundaries in seconds, from 5 ms to
  10 s. Before, SDKs used their millisecond buckets, every value fell into the first one, and percentiles meant
  nothing.
- The `MethodCache.distributed` span is `Internal` instead of `Client`, so the Redis and SqlClient client spans nest
  under it.

HTTP caching
- `HttpCacheOptions.MaxResponseSize` is now `MaxResponseBytes`. Zero or less fails at startup; before, `0` meant no
  limit. Write `long.MaxValue` for no limit.
- `HttpCacheOptions.CacheableMethods` is removed. GET and HEAD responses are cached, as before; a POST or PUT added to
  the set was ignored anyway.
- `AddDiagnosticHeaders` adds a `MethodCache` member to the RFC 9211 `Cache-Status` header, such as
  `Cache-Status: MethodCache; hit; ttl=42`, instead of `X-Cache`, `X-Cache-Age` and `X-Cache-TTL`. Behind a CDN that
  sets `X-Cache` itself, MethodCache's outcome was hidden.
- A GET or HEAD whose response replaces a stored one that can't be retired throws `HttpCacheInvalidationException`,
  like a write, with a message that says a retry is safe. Before, it threw `CacheInvalidationQueuedException`, whose
  message said the origin mutation may have succeeded.

Add-on packages
- `RedisOptions.Configuration` has no default, and startup fails when neither `Configuration` nor
  `ConnectionMultiplexerFactory` is set. Before, it was `localhost:6379`, so a missing configuration section made the
  app run without a distributed cache and without invalidation between instances, silently. Write
  `UseRedis("localhost:6379")` for a local Redis.
- `UseMessagePack(MessagePackSerializerOptions)` is now `UseMessagePack(o => ...)`, which receives MethodCache's
  defaults: `UseMessagePack(o => o.WithCompression(MessagePackCompression.None))` keeps `UntrustedData` and the
  contractless resolver. Before, options built from `MessagePackSerializerOptions.Standard` dropped them without
  notice.

### Added

- `IMethodCacheKeys`, resolved from dependency injection, returns the key of a call to a cached method,
  `keys.For<IUserService>(users => users.GetUser(42))`, and the method's id, `keys.MethodId<IUserService>(...)`.
  Overloads, versions, generic methods and keyed registrations are handled like the real call, so you no longer write
  `"FindAsync(string)"` or the version yourself.
- `ICacheAdministrator.InvalidateMethodAsync(methodId)` removes every result of one method, on every instance, and
  `POST {prefix}/invalidate-method` does the same over HTTP. `CacheEntryInfo.MethodId` and
  `ListEntries(methodId: ...)` (`GET entries?methodId=`) show one method's entries. An id that no cached method has
  throws an `ArgumentException`, and the endpoints answer 400.
- A `"Groups"` section in `appsettings.json` sets a group's policy, with the settings of `Defaults` plus `ClearTags`.
  It wins over `mc.Group(...)` in code; a method's own settings win over both.
- `mc.Distributed(o => ...)` and `mc.Telemetry(o => ...)` configure `DistributedCacheOptions` and
  `MethodCacheTelemetryOptions`, so you don't need `services.Configure<...>` for them. `MethodCacheBuilder.Services`
  is public, for a package that adds its own `mc.UseXxx()`.
- `SlidingExpiration` and `RefreshAhead` accept `"off"` in `[Cache]` and `appsettings.json`, and `TimeSpan.Zero` in
  `WithSlidingExpiration` and `WithRefreshAhead`, to turn an inherited window off for one method, as `FailSafe`
  already did. Before, a method could not turn them off.
- `CacheInvalidationQueuedException.Result` holds the counts of the invalidation that is still queued (`null` after
  `ClearAsync`), and the exception has the standard constructors.
- `GetOrCreateManyAsync` takes built `CacheEntryOptions`, so a hot bulk call can reuse them, as `GetOrCreateAsync`
  can.

### Fixed

- Several registrations of one interface, such as two `IPriceProvider` implementations resolved as
  `IEnumerable<IPriceProvider>`, shared cached results, so every provider returned the first one's answer. Each
  implementation type now caches its own results; its keys carry the type. A single registration keeps its keys.
- A keyed service whose constructor takes a `[ServiceKey]` parameter got MethodCache's internal key and couldn't be
  resolved. It gets your key, as without MethodCache.
- Resolving a cached interface with `KeyedService.AnyKey` also returned the implementations without caching. It
  returns only the cached ones.
- `GetOrCreateManyAsync` kept a cancelled caller waiting while a factory that ignored the token ran. The caller stops
  waiting, and the batch finishes in the background and is cached, as with `GetOrCreateAsync`.
- `GetOrCreateManyAsync` threw when the factory failed, where `GetOrCreateAsync` served the fail-safe value. Each key
  now gets its fail-safe value; the factory's exception is thrown only when a key has none.
- A typed `.CacheWhen(...)` on `mc.Method(...)` won over a later `.Configure(o => o.CacheWhen(...))`. The last call
  wins.
- `GetOverridesAsync` reported `ClearsTags = false` for an override that cleared tags; `GetOverrides` reports it.
- A `ConnectionMultiplexerFactory` ran inside a lock on the thread of the first cached call, so while Redis was slow or
  down that call and every concurrent one waited for the connect timeout. It runs on the thread pool, and calls are
  served from memory and your method in the meantime.
- `AddHttpCaching` both in `ConfigureHttpClientDefaults` and on a named client added two cache handlers to that
  client. It has one, and every settings delegate applies; use
  `services.Configure<HttpCacheOptions>("name", o => ...)` to give one client other settings.
- Invalid `UseSqlServer` options gave the same general message for every problem. The message names the rule.
- Calling `UseSqlServer` more than once, to add to its options, created the tables once per call at startup, and
  while SQL Server was down each call waited and retried on its own. The tables are created once.
- A rejected `appsettings.json` reload logged only the exception type. It logs the setting's path and what is wrong
  with it, never the value.
- A project's own `<WarningsAsErrors>` replaced MethodCache's CS9057 error, so with a too old SDK the generator was
  skipped with only a warning and nothing was cached. The rule comes from a `.targets` file and keeps the project's
  list.
- `mc.Distributed(...)`, `UseMessagePack()` or `UseJson(...)` without `UseRedis` or `UseSqlServer` did nothing without
  a word. Startup logs a warning.

## 1.0.0-rc.1 (2026-10-19)

The first public release on nuget.org, as a release candidate. Install it with `--prerelease`
(`dotnet add package MethodCache --prerelease`). The public API can still change before 1.0.0 if the release candidate
shows a problem; from 1.0.0 on, MethodCache follows semantic versioning.

| Package | What it adds |
|---|---|
| `MethodCache` | `[Cache]` and `[CacheInvalidate]`, the source generator, the analyzers and the in-memory cache |
| `MethodCache.Redis` | Redis as the distributed cache, with a pub/sub backplane that keeps every instance's memory in sync |
| `MethodCache.SqlServer` | SQL Server as the distributed cache, with a polling backplane |
| `MethodCache.Serialization.MessagePack` | MessagePack instead of System.Text.Json for distributed values |
| `MethodCache.AspNetCore` | Admin endpoints and a health check |
| `MethodCache.OpenTelemetry` | Traces and metrics for cache operations |
| `MethodCache.HttpCaching` | HTTP caching for `HttpClient`: ETag and Last-Modified revalidation, Cache-Control and Vary |

All packages require .NET 10 and the .NET 10 SDK.

### Changes since the pre-release builds

Only relevant if you used a build from GitHub Packages or from this repository before the first nuget.org release.

- Retry transient Redis subscription-recovery failures without waiting for another socket event,
  preserving single delivery, generation guards and cancellable shutdown.

- Every package carries the MethodCache logo as its NuGet icon.

- Keep logical-key framing consistent across storage, lookups and invalidation, including leading NUL.
  Custom managers now accept and forward the optional version on `TryGetAsync` and `InvalidateKeysAsync`.
- Use SipHash-1-3 with a secret 128-bit key kept for the process lifetime for memory-key lookups.
  Prefix reuse preserves exact UTF-16 key text, equality, L2 addresses and invalidation identities.
- Keep one Redis callback per subscription after repeated reconnects. Incompatible L2 scopes fall back
  to memory, retain invalidations and report an actionable unhealthy status.
- Reject durations that contain fractional ticks, preserve MC0004 for invalid durations, and report
  each shared attribute error once when generator and analyzer run together.
- Report HTTP/admin invalidations awaiting distributed confirmation as 503; accept long client
  names through bounded hashed identities and validate static tags at startup.
- Before release, narrow and refreeze generated contract 1: all inline key shapes use a bound builder.
  Rebuild applications using earlier development snapshots together with this Core.

Diagnostic migration: MC0001 stays MC0001; duration MC0004 stays MC0004; MCG001 → MC0003; MCG002 → MC0022; MCG003–MCG007 → MC0005–MC0009; MCG009–MCG019 → MC0010–MC0020. MC0021 rejects explicit negative versions. Update analyzer suppressions and `.editorconfig`; migrate `[Cache("group")]` to `[Cache(Group = "group")]` and remove `[CacheKey]`. Canonical `ICacheKeyProvider` parts and manual `ICacheManager` keys replace raw-key use cases.

- Avoid the implicit client-tag index on local HTTP publication. Client-tag invalidation scans resident HTTP
  entries and physically reclaims matching bodies; URL and other application-tag indexes remain.

- Keep sparse tag indexes small and materialize local HTTP epoch/URL metadata only when needed.
  Administration, generation identities and conservative byte-budget accounting are preserved.

- Reuse already successful HTTP origin tasks while recording the same load duration, errors and activity parent.

- Reuse an entry's existing process-keyed hash when evicting or expiring it, avoiding redundant hashing
  during cache pressure without changing key identity or eviction behavior.

- Reuse the latest SQL Server epoch command shape instead of rebuilding its SQL text on every HTTP lookup.
  Generation values remain fresh; reordered and duplicate keys retain their caller positions.

- Avoid temporary sets and sorting when HTTP responses have no custom key headers or `Vary` fields;
  private caches still select by `Authorization` and `Cookie`.

- Avoid allocating unused fail-safe and distributed-write closures on memory-only asynchronous misses.

- Read already initialized, permanent SQL Server HTTP epoch metadata without rewriting its rows. Missing,
  expired or finite/sliding metadata is still initialized or repaired under the same atomic locks.

- Keep generated source filenames bounded for long namespace/type identifiers, including Unicode, when
  `EmitCompilerGeneratedFiles` is enabled. Generated types and cache identities are unchanged.

- **Breaking before 1.0:** all runtime packages now require .NET 10 and the .NET 10 SDK. Remove .NET 8/9 targets,
  their CI/package matrix and the allocating pre-.NET 9 lookup fallback. Generated contract 1 is refrozen before the first release.

#### Core telemetry and attribute diagnostics

- Move instrumentation into Core's container-owned Meter/ActivitySource; the OpenTelemetry package now only subscribes.
  Replace provider/wrapper APIs with `MethodCacheTelemetryOptions`, one final request outcome and seconds histograms.
  Spans/logs omit exception payloads and copied request identities; key/tag recording is explicit opt-in.
- Replace object-memory estimates with local method/HTTP counts, HTTP bytes, observed distributed entries and
  `TotalWeight` in configured units. Health checks honor `failureStatus` and distinguish L2 availability from actual
  backplane connection state and the two recovery queues.


- Unify duration parsing and generator errors; add named attribute groups, enabled/distributed flags and fail-safe retry delay. Remove raw-key attributes and consolidate diagnostic IDs. Dynamic tag placeholders now use canonical key parts while existing contract-1 binaries retain their formatting.

- Omit invalid reload values and policy watcher exception payloads from diagnostic logs; retain error types.
- Drain HTTP generation recovery before disposing its transport. Retry once through an open breaker and report
  unconfirmed URL invalidations at shutdown without exposing targets.

#### Core-owned HTTP storage

- Derive local HTTP administration IDs when first inspected, avoiding the hash and ID allocation on cold publication.
  IDs, removal behavior and conservative response-budget accounting remain unchanged.

- Merge 304 response headers directly into the replacement representation, avoiding a temporary dictionary
  and per-response filter allocations when no Connection options are present.

- Avoid constructing duplicate response/content/header objects during foreground 304 revalidation; validate
  the returned representation before adding caller-only headers, and preserve background/error ownership.

- Release every retired local response body when its URL generation slot is invalidated, including colliding URLs.
  Avoid temporary identity/target buffers and reuse immutable local metadata during response replacement.

- Require explicit AddMethodCache alongside AddHttpCaching, in either order. Core owns a separate 100 MiB
  HTTP byte budget, or exclusively uses MethodCache's native Redis/SQL Server transport. Remove the public
  HTTP storage options/interface/entry extension points and the application-cache fallback.
- Capture shared atomic URL/client/static-tag/clear generations before origin work. Confirm unsafe mutation
  invalidation across nodes and value formats even with lost backplane messages; retain owned recovery when
  callers cancel. Unconfirmed mutations throw HttpCacheInvalidationException and must not be retried automatically.
- Add KeepCookies for a complete response-cache bypass, static HTTP tags, resident/observed administration and
  bounded body-free L2 observations. Core telemetry and health distinguish HTTP capability from method statistics.

#### Owned distributed transports and stable wire contract

- Freeze one Core prefix and validated provider options at startup; use owned storage and serializer registrations. Application `IDistributedCache` is never a fallback. SQL connection strings can come from DI.
- Add wire v1 and entry format 6 with absolute deadlines, format-independent logical invalidation and shared retention bounds. Malformed mandatory frames clear local entries and warn once.
- Add native batched reads and permanent atomic epochs in Redis/SQL Server; version SQL schemas and ship an explicit legacy migration.
- Select JSON through copied options or a source-generated context. Add `VerifyRoundTrip` and `SerializerVersion`; internalize serializer/atomic-store interfaces.

#### Cache API and policy isolation

- Reduce ICacheManager to four async operations with immutable options and explicit invalidation results.
- Retire local generations on effective policy changes, isolate runtime L2 namespaces and validate L2 lifetimes from first publication.
- Internalize policy/key/backplane implementation APIs and remove public cache callbacks and memory tuning switches.
- Keep the replaced fluent root/service/group coordinators internal; startup configuration uses `MethodCacheBuilder`
  and retains its public method configuration results.

#### Generated contract and explicit startup registration

- Generated decorators use the versioned `MethodCache.Generated` facade. Each invocation captures one immutable
  policy/key snapshot; retired snapshots bypass caching and cannot publish into the current generation.
- Startup uses `MethodCacheBuilder`, with `Defaults`, `Group`, typed `Method` expressions and explicit module
  selection. Missing services require `AutoRegister(lifetime)` and exactly one accessible implementation.
  Cacheable late, open generic, catch-all keyed and unsupported bindings produce named errors.
- Built-in synchronous methods use a synchronous factory and single-flight path. Async-only custom managers
  bypass synchronous caching with a diagnostic. Distributed adapters without bounded native synchronous
  operations are unavailable for these calls; use async methods for L2 and distributed mutation invalidation.
- All six add-on packages require the exact matching Core package version. Frozen vendor binaries are tested
  under the current runtime and Native AOT without rebuilding their source during ordinary tests.

#### Project review fixes

- Bulk factory results invalidated during their source read are not published; concurrent individual writes still win.
- Collection subclasses keep their public state in default, JSON and MessagePack keys. Collection subtypes needing runtime-state serialization now use hashed keys; earlier inline keys for those subtypes become cold misses.
- Static tags skip argument formatting, and dynamic tags format only referenced parameters.
- Redis backplane publication honors cancellation, and cancelled unsubscribe acknowledgements no longer fail application shutdown. SQL backplane gaps survive slow transactions; a separate non-waiting probe retires confirmed absent identities while newer invalidations and other committed gaps keep arriving.
- HTTP full replacement responses retire older entries when they cannot be stored. Stale-while-revalidate honors request freshness limits, and concurrent first variants share an index without hiding each other. Variant indexes outlive their bodies, and selection-rule changes retire the URL epoch once, preserving concurrent publications in the new epoch even when deleting the old entry fails after rotation.
- Backplane gap failure coverage runs without infrastructure in the Core fault-injection suite; real SQL lock checks supplement it.
- Memory benchmarks measure fresh fills and real tag invalidations; short-lived entries use the intended duration.
- SQL Server pins patched PKCS dependency versions for [CVE-2026-84008](https://github.com/advisories/GHSA-846f-f764-76mx), restoring the high-severity dependency audit gate.

#### Remove retained compatibility code

The previous memory engine benchmark baseline is removed, and current runtime-key benchmarks have descriptive
names. SQL Server backplane initialization requires `Scope binary(32) NOT NULL`; automatic migration and cleanup
of unscoped rows are removed. Schema errors are logged, and table initialization uses the registered
`TimeProvider` for its startup deadline and retry interval. Benchmark `--list` commands no longer throw.

#### Release audit fixes

The release audit fixes presence-aware manual reads, caller cancellation during loads, immutable policy tags,
HTTP response age and background request metadata, keyed registrations and OpenTelemetry ownership, generic
method constraints and attribute code fixes, and weighted capacity overflow. The web demo now stores atomic
price updates in SQLite, and the memory benchmark measures an uncached baseline.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements.
Earlier keys for these arguments become cold misses. Overloaded generic methods include generic arity in their
policy ids and key prefixes, keeping different overloads distinct; earlier keys become cold misses.

#### Third review fixes

- **HTTP caching** no longer stores a redirect the transport followed under the original URL (the transport drops
  `Authorization`, so one caller's response could reach another), invalidates the URL a `POST` was sent to also when
  it answers `303`, and removes its own validators so a retry of the same request gets a full response.
  `Vary: Accept-Encoding` works with automatic decompression, a `GET` with a body bypasses the cache, a
  `Cache-Control` or `Vary` that doesn't parse is not stored, clients that get caching from
  `ConfigureHttpClientDefaults` keep their own entries, far-future `Expires` no longer overflows, weak ETags stay weak,
  and the URL fragment is not part of the key. HTTP cache keys change, so entries from an earlier build are misses.
- **Loading and fail-safe**: a value invalidated or replaced while (or just before) its reload ran is no longer
  served as the fail-safe fallback to callers that joined the reload or a failed background refresh. A factory that
  requests the key it is loading now throws `InvalidOperationException` instead of hanging that key for every later
  caller. While a fail-safe value is reused, refresh-ahead no longer retries the method before `retryAfter`.
- **Refresh-ahead**: a `RefreshAhead` not shorter than `Duration` or `SlidingExpiration` is rejected by
  `CacheEntryOptions.Builder.Build()` and in `appsettings.json` (startup fails); one that comes from combining
  sources starts the reload halfway through the entry's life instead of reloading on every read.
- **Memory cache**: a manager that is never disposed no longer keeps its values (and the execution context of whoever
  created it) alive through its cleanup timer. `GetOrCreateManyAsync` no longer sends cached keys to the factory with
  `EnableFastPath = false` or inside a refresh window. The default `ICacheManager.TryGetFast` of a custom manager
  asks `LookupAsync`, so a cached `0`, `false` or `null` is a hit. `WithDuration`'s error message no longer promises
  entries that never expire.
- **Distributed cache**: a tag invalidation or clear another instance announces while a stamp read is on the wire is
  no longer forgotten (which promoted invalidated entries into memory). Invalidations queued during an outage are
  stored in batches, so a large backlog drains; more than 10,000, or one the cache keeps refusing, become one clear.
  On shutdown the queue is tried once more, after a retry already running (which stops after the batch it is storing), and what is lost is logged. Keys whose
  write timed out are removed at shutdown instead of after a delay nothing waits for, and further timed-out writes
  of a key push its one removal back instead of adding another. Shutdown also waits for writes still running (each capped
  by the operation timeout), so the removal one of them leaves behind is stored with the rest of the queue. A write that times out no longer opens the
  circuit breaker for the whole instance. A tag or clear stamp from a server whose clock runs ahead no longer turns
  every entry written since into a miss.
- **Serialization**: the default `JsonCacheSerializer` includes public fields, so value tuples and structs with
  fields are shared through the distributed cache instead of coming back empty. Each value written is read back
  first; values that don't survive that (private setters, a derived type
  returned as its base type, `object`) stay in memory only, with one warning, instead of handing other instances a
  value with missing data. Collections declared as interfaces are still shared when they come back as another
  implementation. A derived value nested in a collection or property isn't detected. Including fields changes two
  things: a type with a public field the serializer can't write (a delegate, a reference back to the object) now
  fails to serialize, and a public readonly field of a type with a parameterless constructor doesn't read back.
  Both now stay in memory only with one warning; before, the field was left out and the rest was shared. A value the
  serializer throws on now warns once per method and type instead of on every write. Entries an earlier build wrote
  for types with public fields are read without those fields until they expire.
- **Redis**: a connection factory that threw once (Redis down at first use) is asked again on the next call instead
  of failing until restart, and the backplane retries its subscription with backoff (1 s doubling to 30 s, a warning
  per failure) instead of never subscribing. Once it subscribes after failing, it clears this instance's memory,
  as after a reconnect, since invalidations sent meanwhile never arrived. The docs now say to run Redis with `maxmemory-policy noeviction`: an evicted invalidation
  stamp brings invalidated values back.
- **SQL Server**: a `TagRetention` that ends after 9999-12-31 fails at startup instead of making every tag
  invalidation fail, and stamp expiry is capped there. Expired rows are deleted every 5 minutes instead of 30, so each
  delete stays small.
- **Generated decorators** compile for generic methods constrained to a base class (`where T : Entity`), keep
  `scoped` on forwarded parameters, escape U+2028/U+2029 in tag and group literals, skip `file` classes when picking
  an implementation to register, and no longer clash (CS0436) with a referenced project's generated code through
  `InternalsVisibleTo`. `[Cache]` or `[CacheInvalidate]` on a `static`, non-public or `sealed` interface member is now
  warning MCG017 instead of being ignored. The IDE builds each decorator once per edit, not once per cached method.
- **Keys**: two cached methods with the same name on different interfaces of one hierarchy (`ICatalog : ILookup`, or
  `IC : IA, IB`) no longer share entries; their keys add the parameter types, and the declaring interface when the
  signatures are equal. `-0.0` and `0.0` are two keys in hashed keys too, `NaN` and infinities inside a hashed argument
  no longer throw, and `[CacheKey(UseAsRawKey = true)]` dates are written like generated keys: they keep fractions of a second and
  their offset, and a local time is written as UTC, so the key doesn't depend on the server's time zone. Keys of
  these methods and arguments change, so their entries from an earlier build are misses.
- **Metrics and spans** name a cached method by its id (`MyApp.IUserService.GetAsync`) instead of its bare name, so
  same-named methods on different interfaces are separate series, as the OpenTelemetry README shows. A method
  inherited from a generic interface reports the closed type it comes from, so `IUserRepo : IRepository<User>` and
  `IOrderRepo : IRepository<Order>` report `IRepository<User>.Get` and `IRepository<Order>.Get`.
- **Framework types in keys**: `BigInteger`, `Complex`, `IPAddress`, `JsonElement`, `Half`, `Int128`, `DateOnly`,
  `Uri`, `Version` and other immutable .NET types are written by their value, in every key generator and inside
  records and collections. Before, the decorator wrote `BigInteger`'s `IsZero` and `Sign`, so 3 and 5 shared one
  entry, and `IPAddress` threw. Other types from .NET, ASP.NET Core or Microsoft.Extensions in a key, such as
  `StringBuilder` or `CultureInfo`, are now build error MCG018 instead of a key that ignores their value. Keys of arguments with these
  types change, so their entries from an earlier build are misses.
- **Result types a distributed cache can't restore** (`object`, an interface or an abstract class, also as an
  element) get warning MCG019: with a distributed cache, the default round-trip check keeps those results in memory
  only.
- **`[assembly: CacheInterface]` in a class library** decorates the client even when none of the library's code has
  run before `AddMethodCache()`, also when several threads resolve services while the library's initializer runs.
- **Dependency injection.** Generated decorators implement `IDisposable`/`IAsyncDisposable` only when the interface
  does, so the container no longer keeps every transient decorator resolved from the root (their `ownsDecorated`
  constructor parameter is gone). Keyed registrations are decorated under their own key, with the service key in
  their cache keys (`MyApp.IRates.Get@ecb:1`, see `MethodCacheKeys.KeyedMethodName`), so two registrations of one
  interface never share entries; a key that isn't a string, enum, bool, char, integer or `Guid` makes
  `AddMethodCache()` throw. An interface registered only keyed no longer gets an extra unkeyed singleton. When two assemblies generate a decorator for the same
  interface (`[assembly: CacheInterface]` in a library and in the app), the one in the assemblies passed to
  `AddMethodCache` (by default the caller's) or else the entry assembly's is used, instead of whichever module
  initializer ran last; otherwise `AddMethodCache` throws naming both.
- **Startup validation.** The generic host loads every policy source when it starts, so startup-code
  misconfiguration and invalid appsettings fail `StartAsync` and unknown method ids are logged then, not on the first
  call. `AddMethodCacheFromConfiguration` reads its section at that point, so configuration sources added after the
  call count. appsettings now rejects unknown setting names, a single value for `Tags`, an object for a method's
  `FailSafe` and time spans without `:` (`"300"` was 300 days). The resolved policy of a method keeps the newest
  version when two sources change it concurrently. The `MethodId` property for environment variables is documented.
- **OpenTelemetry and admin.** `ICacheMetricsProvider.CacheLatency` takes a `TimeSpan`, and `load_duration` keeps
  sub-millisecond precision. `EnableMetrics = false` keeps the app's own metrics provider, and options from several
  calls or `Configure<OpenTelemetryOptions>` compose instead of resetting. A hit no longer writes a shared counter
  when nothing listens; the hit ratio comes from the cache's statistics. `SetCacheUserId(null)` and
  `SetCacheTenantId(null)` clear the value. Admin `DELETE entry` answers 204 with a distributed cache, since the
  removal reached it and the other instances. The web demo's `.http` walkthrough loads product 2 before showing it
  is still cached.
- **Tags combine across policy layers.** Tags from startup code, `appsettings.json`, runtime overrides and the
  defaults are added to the `[Cache]` attribute's tags instead of replacing them, so a `[CacheInvalidate]` on an
  attribute tag always reaches the entry. To replace them, call the new `CacheEntryOptions.Builder.ClearTags()`
  (`CachePolicyFields.ClearTags` for a raw policy) or set `"ClearTags": true` for a method in `appsettings.json`.
  The docs no longer say runtime overrides win over everything: a method's own override wins over its other layers,
  while runtime defaults (`DefaultPolicy`, `ForGroup` in `ApplyAsync`) only fill in what the method leaves unset, as
  before.
- **Background loads of scoped services.** A refresh-ahead, and a fail-safe call that goes on after its caller got
  the expired value, now run a scoped or transient service on a new instance from a new DI scope (keyed services
  keep their key), disposed when the load ends, instead of on the request's instance after its scope was disposed.
  Every scoped or transient cached service the load calls gets its own scope, and a call left running after the load
  ended gets none. The scope factory comes from the root container. Singletons are unchanged. Generated decorators call the new `DecoratorServices.ForLoad`, so rebuild the projects
  that generate them together with the new MethodCache.Core.

#### If you used a pre-release build from GitHub Packages

- **Generated `Add{Interface}WithCaching` methods are gone.** Combined with `AddMethodCache()` they registered the
  service twice. Register the interface the usual way (with a factory if you need one), before `AddMethodCache()`.
- **Keys for array and collection arguments.** The decorator now writes them into the key itself
  (`ByIdsAsync:[3:1:2:3]`) instead of hashing them, so they also work under Native AOT. Distributed cache entries
  written by a pre-release build for such methods are misses and expire normally.

- **HTTP cache keys.** Keys are now length-framed, so every HTTP response cache key changes, including for requests
  without `Vary` headers. Entries written by a pre-release build are not read again; this applies to the built-in and
  to custom HTTP cache storage. Let them expire, or clear the storage namespace when you deploy.
- **Distributed invalidation.** Backplane key and tag messages use new message types and lossless UTF-16 encoding,
  and distributed cache entries use envelope version 4. Invalidations do not cross between a pre-release build and
  1.0.0 in either direction: a node ignores backplane messages from another MethodCache version and logs one warning,
  and entries written by a pre-release build are cache misses. Upgrade every node that shares a cache and backplane
  together, with a cache clear, or use a new cache namespace.
- **SQL Server tables.** The invalidations table gets a nullable `Scope binary(32)` column and the indexes
  `Index_Scope_Id (Scope, Id)` and `Index_Scope_CreatedAt (Scope, CreatedAt)`; with `CreateTablesIfMissing = false`,
  add them in your migration before deploying. Rows without a scope are ignored and deleted once they are older than
  `BackplaneRetention`. Cache rows written by a pre-release build (unhashed keys) become misses and expire normally.
  Upgrade every instance that shares the tables together, and restart them with empty in-memory caches.
