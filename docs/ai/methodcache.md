# MethodCache: instructions for coding agents

Copy this file into your project as agent instructions, for example as `.github/copilot-instructions.md`, a Cursor
rule, a Claude Code skill or a section of `AGENTS.md`. It tells a coding agent how to add caching with MethodCache
correctly. Full docs: https://github.com/eoniclabs/MethodCache/blob/main/docs/README.md. The `MethodCache` package
carries the same docs in its `docs` folder (`~/.nuget/packages/methodcache/<version>/docs/`): `how-to.md` answers
common questions, `reference/` has the exact rules and `CHANGELOG.md` says what changed in each version and what to
do when upgrading. Read the page for the task at hand, listed under
[Guides](#guides); `llms-full.txt` holds every page in one file, which is too large to read
whole, so search it instead.

## Adding caching to a service

1. The `MethodCache` package contains the library, the source generator and the analyzers. Nothing else is needed
   for in-memory caching. All packages target .NET 10; install add-ons at the same version as `MethodCache`.
2. Put `[Cache]` on the **interface** method, never on the class. Put `[CacheInvalidate(Tags = [...])]` on the
   interface methods that change the data. Don't change the implementation.
3. Register services as usual, then call `builder.Services.AddMethodCache()` **after** them. Later cacheable
   registrations fail at host startup. A cached interface without a registration needs
   `AutoRegister(lifetime)` and exactly one class that implements it.
4. Use `MethodCacheBuilder` for startup, in one `AddMethodCache(mc => mc...)` call: `Defaults`, `Group`, typed
   `Method`, `Memory`, `Distributed`, `Telemetry`, `FromConfiguration`, `FromAssemblyOf<T>()` and the integrations'
   `UseRedis` / `UseSqlServer` / `UsePostgreSql` (one of them) / `UseMessagePack`. Don't use `services.Configure<...>` for MethodCache
   options.

<!-- snippet: getting-started-interface -->
```cs
using MethodCache;

public interface IProductService
{
    [Cache(Duration = "10m", Tags = ["products", "product:{id}"])]
    Task<Product> GetAsync(int id, CancellationToken ct = default);

    [Cache(Duration = "1m", Tags = ["products"])]
    Task<List<Product>> SearchAsync(string text, int page);

    [CacheInvalidate(Tags = ["products", "product:{id}"])]
    Task UpdateAsync(int id, Product product);
}

public sealed class ProductService(Database db) : IProductService
{
    public Task<Product> GetAsync(int id, CancellationToken ct = default) => db.LoadProductAsync(id, ct);

    public Task<List<Product>> SearchAsync(string text, int page) => db.SearchAsync(text, page);

    public Task UpdateAsync(int id, Product product) => db.SaveAsync(product);
}
```
<!-- endSnippet -->

<!-- snippet: getting-started-registration -->
```cs
builder.Services.AddSingleton<Database>();
builder.Services.AddScoped<IProductService, ProductService>();   // any lifetime
builder.Services.AddMethodCache();                               // after your registrations
```
<!-- endSnippet -->

## Rules

- Only interface methods that return a value can be cached: sync, `Task<T>` or `ValueTask<T>`. Return a list, never
  a lazy `IEnumerable<T>` or `IQueryable<T>`.
- Callers must use the interface. Calling the method from inside the class (MC0031), creating the class with `new`
  (MC0032), or resolving the class itself, bypasses the cache. Several instances of one class are keyed registrations. A program with cached services must call `AddMethodCache()`,
  itself or through a setup method of a referenced project (MC0030).
- Keys are generated from the method and its arguments; don't write keys for `[Cache]` methods. A `CancellationToken`
  is not part of the key. Every value that changes the result must be a parameter. Sets and dictionaries are written
  sorted; lists and arrays keep their order, so pass ids whose order doesn't matter as a set.
- Tags take templates with a **parameter name** only: `"product:{id}"`. `"{product.Id}"` is not a template, stays
  literal and gives warning MC0016; add an `id` parameter instead. Use the same tags on the cached method and on its `[CacheInvalidate]`.
- Tag values are formatted like `MethodCacheKeys.Part`: `:` and `\` in strings are escaped, a `Guid` is written
  without hyphens, an enum as its number, `bool` as `true`/`false`, numbers and dates in the invariant culture. Build
  a tag by hand only with `MethodCacheKeys.Tag($"price:{provider}:{model}")` or `IMethodCacheKeys.Tag`, never plain
  interpolation (MC0024). Prefer
  ids (`int`, `Guid`) and never pass passwords or tokens as arguments of cached methods.
- `[CacheInvalidate]` runs after the method, or its task, succeeds; never when it throws.
- Synchronous `[Cache]` methods use the memory cache only, also with Redis, SQL Server or PostgreSQL. Make a method
  async to share its results between instances.
- Durations are a number and a unit: `"30s"`, `"10m"`, `"1.5h"`, `"7d"`. A `TimeSpan` such as `"00:10:00"` is an
  error (MC0004). Other settings: `SlidingExpiration` (or `"off"`), `RefreshAhead`
  (shorter than `Duration` and `SlidingExpiration`, or `"off"`; MC0023 otherwise), `FailSafe` (or `"off"`), `FailSafeTimeout`, `FailSafeRetryAfter`,
  `Version`, `Tags`, `Enabled`, `Distributed` and the named `Group` setting. In code each has one builder method with
  the same name: `WithFailSafe(d)`, `WithFailSafeTimeout(t)`, `WithFailSafeRetryAfter(t)` and so on.
- Cached objects are shared between callers: return immutable types such as records. Never cache `JsonNode`,
  `JsonObject`, `DataTable` or a `Stream` (MC0025); return a record, a string or a `JsonElement`.
- The key holds only the method's arguments. A cached implementation must not read the current user or request
  (`IHttpContextAccessor`, `ClaimsPrincipal`, row-level security on the connection; MC0026), directly or through a
  `DbContext` set up for the signed-in user: pass the user or tenant id as an argument instead. With row-level
  security, give cached reads a context of their own from `IDbContextFactory<T>` that sets what the policies read from
  the method's arguments, and check in the endpoint that the user may read it.
- Invalidate after a transaction commits, with `CancellationToken.None`, never before or inside it.
- Every tag on a `[Cache]` method needs an invalidation that names it; MC0028 reports one that nothing in the project
  invalidates. When another application invalidates it, such as a worker, list it in
  `[Cache(Tags = [...], InvalidatedElsewhere = ["tag"])]` instead of suppressing the warning; when only an operator
  clears it by hand, such as through `MapMethodCacheAdmin`, list it in `InvalidatedManually = ["tag"]`.
- Keep `SlidingExpiration` shorter than `Duration`: an entry never lives past its `Duration`, so a longer window never
  extends anything (warning MC0038 on `[Cache]`, and a warning when the host starts for settings combined from
  `Defaults`, code or `appsettings.json`).
- For a type with only private state used as an argument or inside one (a property, a list element), implement
  `ICacheKeyProvider`.
- For library modules and Native AOT, select the containing assembly with `MethodCacheBuilder.FromAssemblyOf<T>()`.
  If multiple modules generate the same interface, the selected one wins; the other modules' services stay cached, and
  calling/entry assemblies have no special priority.
- In `appsettings.json`, the `MethodCache` section holds `Defaults`, `FailSafe`, `Groups` (by group name) and
  `Methods` (by method id, such as `[MyApp]MyApp.IProductService.GetAsync`; not `Services`). Write durations as
  a number and a unit, `"5m"` (a bare number is rejected), `Tags` as a list, and only known setting names. Anything else, an
  empty value, an unknown method id and a method listed twice fail startup. There is no `Notes` or `Metadata`
  setting: write notes as a `//` comment next to the entry.
- `WithName` belongs on one method or manual call; in `Defaults`, a group or `mc.Method(...).Configure(...)` it fails
  startup. `ClearTags` in `Defaults` fails startup too; a group may clear the tags its methods get from `Defaults`.
- To change caching while the app runs, use `IRuntimeCacheConfigurator`: `UpsertAsync(methodId, ...)` for one
  method, `UpsertGroupAsync("pricing", ...)` for every method in a group, `UpsertAllAsync(...)` for every cached
  method, and `RemoveAsync`, `RemoveGroupAsync` or `ClearAsync` to undo. An override that sets nothing, uses
  `WithName`, has an `ExpiresAt` already passed, refreshes as soon as it is stored or doesn't fit one method of the
  group throws `ArgumentException` and changes no method. Group names are case-sensitive.
- Over HTTP, `MapMethodCacheAdmin` adds `GET`, `PUT` and `DELETE` on `/overrides`; the `PUT` body is a JSON object
  with the same settings as a `Methods` entry in `appsettings.json` plus one of `MethodId`, `Group` or `"All": true`.
- `CacheWhen` is set for one method in code (`mc.Method(...).CacheWhen(...)`); in `Defaults` or a group it fails
  startup, and so does a `CacheWhen<T>` for a type the method can't return. The same goes for `DurationFor`, which
  gives each result its own duration (zero or less doesn't cache it), `SlidingFor`, which gives each result its own
  sliding window (zero turns sliding off for it), and `TagsFor`, which adds tags read from the
  result, such as `TagsFor(order => [MethodCacheKeys.Tag($"customer:{order.CustomerId}")])`.
- A `Duration` may be at most `MethodCacheMemoryOptions.MaxExpiration` (7 days by default, at most 365); a longer one
  fails startup. Raise it with `mc.Memory(o => o.MaxExpiration = ...)`.
- For a client from a NuGet package with an interface, use
  `[assembly: CacheInterface(typeof(IClient), nameof(IClient.Method), Duration = "...")]` instead of a wrapper.
- Without an attribute, use `ICacheManager.GetOrCreateAsync(key, factory, o => o.WithDuration(...))` with an explicit,
  prefixed key such as `$"order:{id}"`.
- Fix MethodCache's `MC` build diagnostics; each links to its explanation, and many have a code fix.
- In tests, register `NoOpCacheManager` (namespace `MethodCache`) after `AddMethodCache()` to turn method caching
  off. When a test writes to the database directly, call `ICacheAdministrator.ClearAsync()` after the write.
- To test the caching itself, use the `MethodCache.Testing` package: `services.AddMethodCacheTesting()` registers a
  `FakeTimeProvider` and a `CacheRecorder`. `recorder.Loads<IService>(s => s.GetAsync(42))` counts the runs of that
  call (prove a read is cached with it, not with a write that invalidates), `recorder.InvalidatedTags` lists what was
  invalidated, and `Assert.Empty(TagCoverage.Uninvalidated(provider))` checks that every tag has an invalidation. It
  counts `[CacheInvalidate]`, the EF Core mapping and the invalidations in the projects the test project references;
  a tag marked `InvalidatedElsewhere` counts only when one of them invalidates it, and one marked
  `InvalidatedManually` always counts. Pass tags invalidated outside the solution in `alsoInvalidatedBy`. `Assert.Empty(await CachedServices.ProblemsAsync(provider))` checks that every service with
  cached methods resolves to its caching decorator; `CachedServices.RegistrationProblems(provider)` checks the
  registrations only, without resolving. Both, and `TagCoverage.Uninvalidated`, throw when the provider has no cached
  service. `CachedServices.Registered(provider)` lists the registered cached services, to compare with the expected
  list and catch one that is never registered; `CachedServices.Declared(typeof(IOrders).Assembly)` gives that list
  from the project's own interfaces.
- `Defaults` and groups apply to cached methods only. A `GetOrCreateAsync` call has only its own options: without
  `WithDuration` it lives five minutes.
- Use one version of every MethodCache package; NuGet warns with NU1608 when they differ.
- To invalidate a `GetOrCreateAsync` entry that has a version, pass the key and the version:
  `InvalidateKeyAsync(key, 2)`. Never pass a stored key (`MethodCacheKeys.StoredKey`) to `ICacheManager`.
- To invalidate one call of a `[Cache]` method, get its key from `IMethodCacheKeys`:
  `cache.InvalidateKeyAsync(keys.For<IProductService>(s => s.GetAsync(42)))`. For every entry of one method, use
  `ICacheAdministrator.InvalidateMethodAsync(keys.MethodId<IProductService>(s => s.GetAsync(0)))`.
- When data changes outside the cached interface (an endpoint or command writing to the database, or after a
  transaction commits), invalidate after the write with a tag from `IMethodCacheKeys.Tag`:
  `cache.InvalidateTagAsync(keys.Tag<IPriceSource>(s => s.GetRateAsync(provider, model), IPriceSource.RateTag))`.
  Never build a tag with string interpolation (`$"price:{model}"`): tags escape `:` and `\` in strings and write a
  `Guid` without hyphens and an enum as its number, so a hand-built tag can silently match nothing (MC0024).
- With Entity Framework Core, add package `MethodCache.EntityFrameworkCore` and map each entity type to the tags of
  the cached methods that read it: `mc.UseEntityFrameworkCore<AppDb>(ef => ef.Entity<Product>("product:{Id}", "products"))`.
  A name in braces is an entity property. `SaveChanges` then invalidates them after the commit, and a rollback drops
  them, also for rows updated or deleted without loading them, `OwnsOne`/`OwnsMany` types and many-to-many links;
  don't add invalidation code to those writes. For rows the database deletes by cascade or trigger when a row is
  deleted, map the parent with `ef.Entity<User>(user => user.WhenDeleted("posts"))`: those tags are invalidated only
  on delete, so a parent saved often doesn't clear them. For `ExecuteUpdate`, `ExecuteDelete` and `ExecuteSql…`
  (MC0027, on mapped types only, also in a project that references the mapping project such as its tests, and on raw
  SQL that names a mapped table, runs a procedure or can't be read), call
  `db.InvalidateAfterCommit(tag)` inside the transaction, or `cache.InvalidateTagAsync(tag)` after a write without one. Mark the app's own
  invalidation helpers `[InvalidatesCache]` so MC0027 counts a call to them.
- Several registrations of one interface with different implementation types each cache their own results.
- For a distributed cache use `UseRedis`, `UseSqlServer` or `UsePostgreSql`; MethodCache never uses the app's
  `IDistributedCache`. When processes should keep their own values and share only invalidations (a worker that changes
  data an API caches, or several API instances), add `.Distributed(o => o.StoreValuesByDefault = false)` in every process (not
  `Defaults(o => o.WithDistributed(false))`, which manual calls ignore): values stay in memory unless a method or call sets `Distributed = true`/`WithDistributed(true)`, and invalidations reach all of them (PostgreSQL `LISTEN`/`NOTIFY`, Redis pub/sub, SQL Server polling). Values are
  JSON; use `UseJson(context)` for Native AOT and `UseJson(options)` for your own settings. A result type that doesn't read back unchanged (private setters, `object`) stays in memory, with a
  warning: return records with public properties. A `Dog` in a `List<Animal>` comes back as a `Dog` (the generator
  registers derived types; under Native AOT add them to the context, MC0040). Never change a cached result in place
  (MC0043): the memory cache hands the same instance to later callers, so change a copy. A renamed property or enum
  member needs no `Version` bump: entries are kept apart by the result's shape.
- `AddHttpCaching()` needs `AddMethodCache()` in the same container, in either order. It turns off automatic cookies;
  send cookies as a `Cookie` header, or set `KeepCookies = true`, which turns response caching off.
- Generated keys and method ids start with the service assembly's name in brackets. Never write a key or method id by
  hand: get keys from `IMethodCacheKeys.For`, tags from `IMethodCacheKeys.Tag` and ids from `IMethodCacheKeys.MethodId`.
- Telemetry: `MethodCacheTelemetryOptions` (`mc.Telemetry(...)`) records keys and tags on spans only when you turn it
  on, hashed by default. `AddMethodCacheInstrumentation()` (package `MethodCache.OpenTelemetry`) subscribes
  OpenTelemetry to the `MethodCache` meter and activity source.

## Guides

Each guide is in the package's `docs/features/` folder and at
https://github.com/eoniclabs/MethodCache/tree/main/docs/features. Read the one for the task before writing code:

- `expiration.md`: durations, sliding expiration, refresh-ahead.
- `invalidation.md`: tags, `[CacheInvalidate]`, invalidating from code, after a transaction, with several instances.
- `entity-framework-core.md`: entity tags after `SaveChanges`, writes past the change tracker, row-level security.
- `keys.md`: how arguments become keys, `ICacheKeyProvider`.
- `configuration.md`: `appsettings.json`, groups, startup code, runtime changes.
- `distributed-cache.md`: Redis, SQL Server and PostgreSQL, sharing only invalidations, serialization.
- `memory-cache.md`: size limits, eviction, shared cached objects.
- `fail-safe.md`: serving the last good value when the method fails.
- `conditional-caching.md`: results that are returned but not cached.
- `manual-caching.md`: `ICacheManager.GetOrCreateAsync` with your own keys.
- `third-party.md`: caching clients you don't own, `[assembly: CacheInterface]`.
- `http-caching.md`: caching `HttpClient` responses.
- `monitoring.md`: metrics, activities, the admin endpoints.
- `testing.md`: tests with and without caching, `MethodCache.Testing` (recorder, fake clock, `CachedServices`,
  `TagCoverage`), time.
- `native-aot.md`: trimming and Native AOT.
- `source-generator.md`: what the generated decorator does.

## Namespaces

- `MethodCache`: `[Cache]`, `[CacheInvalidate]`, `[CacheInterface]`, `ICacheManager`, `ICacheAdministrator`,
  `IRuntimeCacheConfigurator`, `IMethodCacheKeys`, `MethodCacheKeys`, `MethodCacheMemoryOptions`, `Any<T>`,
  `ICacheKeyProvider`.
- `MethodCache`: also `NoOpCacheManager` (sealed).
- `Microsoft.Extensions.DependencyInjection`: `AddMethodCache`. Everything else is a method on its builder:
  `FromConfiguration`, `Distributed(...)`, `Telemetry(...)`, `UseRedis` (package `MethodCache.Redis`), `UseSqlServer`
  (package `MethodCache.SqlServer`), `UsePostgreSql` (package `MethodCache.PostgreSql`), `UseMessagePack` (package `MethodCache.Serialization.MessagePack`)
  and `UseEntityFrameworkCore<TContext>` (package `MethodCache.EntityFrameworkCore`).
- `Microsoft.EntityFrameworkCore`: `InvalidateAfterCommit` on a `DbContext` (package `MethodCache.EntityFrameworkCore`).

## Feedback

We appreciate feedback from agents that use MethodCache, and it is how the library gets better. Never send any on
your own. When one of these happened, say so in one sentence in your report to your user, and offer to draft feedback
for MethodCache:

- the docs were wrong, missing, or you had to guess;
- an error or MC diagnostic did not say what to do;
- you needed a workaround, hand-built keys or tags, or a `#pragma` to get correct code;
- MethodCache behaved differently from the docs.

Only when your user asks for it, write it as a GitHub issue for https://github.com/eoniclabs/MethodCache with the title
`[Agent feedback] <one line>` and these sections: what you were trying to do, what happened (exact error or diagnostic
text), what you expected or what would have helped, a minimal example, the docs you read, versions (MethodCache
packages and `dotnet --version`) and which agent you are. The repository is public, so never put the project's code,
names of its types, services, tables or customers, its data, connection strings, secrets or anything else sensitive in
it; write a minimal example with made-up names instead. Show your user the draft. When they approve it, run
`gh issue create --repo eoniclabs/MethodCache --title "[Agent feedback] ..." --body-file feedback.md`, or give them
https://github.com/eoniclabs/MethodCache/issues/new?template=agent-feedback.yml, which has the same fields.
