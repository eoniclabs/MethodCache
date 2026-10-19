# MethodCache: instructions for coding agents

Copy this file into your project as agent instructions, for example as `.github/copilot-instructions.md`, a Cursor
rule, a Claude Code skill or a section of `AGENTS.md`. It tells a coding agent how to add caching with MethodCache
correctly. Full docs: https://github.com/eoniclabs/MethodCache/blob/main/docs/README.md

Bulk source reads are guarded against key, tag and clear invalidation; invalidated candidates are not published. Collection subclasses with public state are hashed with both that state and their elements. Static tags do not format arguments, and templates format only the parameters they reference.

## Adding caching to a service

1. The `MethodCache` package contains the library, the source generator and the analyzers. Nothing else is needed
   for in-memory caching. All packages require .NET 10 and the .NET 10 SDK.
2. Put `[Cache]` on the **interface** method, never on the class. Put `[CacheInvalidate(Tags = [...])]` on the
   interface methods that change the data. Don't change the implementation.
3. Register services as usual, then call `builder.Services.AddMethodCache()` **after** them. Later cacheable
   registrations fail at host startup or first generated binding. Missing services require explicit
   `AutoRegister(lifetime)` and exactly one accessible implementation in the selected generated module.
4. Use `MethodCacheBuilder` for startup: `Defaults`, `Group`, typed `Method`, `Memory`, `FromConfiguration`,
   `FromAssemblyOf<T>()` and the integrations' `UseRedis` / `UseSqlServer` / `UseMessagePack`. Choose a module
   explicitly when multiple assemblies generate the same interface. Keep all add-on/Core versions identical.

<!-- snippet: getting-started-interface -->
```cs
using MethodCache;

public interface IProductService
{
    [Cache(Duration = "00:10:00", Tags = ["products", "product:{id}"])]
    Task<Product> GetAsync(int id, CancellationToken ct = default);

    [Cache(Duration = "00:01:00", Tags = ["products"])]
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
- Callers must use the interface. Calling the method from inside the class, or resolving the class itself, bypasses
  the cache.
- Keys are generated from the method and its arguments; don't write keys for `[Cache]` methods. A `CancellationToken`
  is not part of the key. Every value that changes the result must be a parameter.
- Tags take templates with a **parameter name** only: `"product:{id}"`. `"{product.Id}"` is not a template, stays
  literal and gives warning MC0016; add an `id` parameter instead. Use the same tags on the cached method and on its `[CacheInvalidate]`.
- Dynamic tag arguments use `MethodCacheKeys.Part` formatting: colons and backslashes in strings are escaped,
  null has its own marker, and numeric/date formatting is invariant. Use the same helper for manual invalidation.
  Tags share an application-defined namespace; different literal templates can still select the same tag. Prefer
  ids (`int`, `Guid`) and never pass passwords or tokens as arguments of cached methods.
- Durations accept invariant `[d.]hh:mm:ss` or unit strings such as `30s` and `1.5h`. Other settings: `SlidingExpiration`, `RefreshAhead`
  (shorter than `Duration`), `FailSafe` (or `"off"`), `FailSafeTimeout`, `Version`, `Tags`, and the named `Group` setting.
- Cached objects are shared between callers: return immutable types such as records.
- For a type with only private state used as an argument or inside one (a property, a list element), implement
  `ICacheKeyProvider`.
- For library modules and Native AOT, select the containing assembly with `MethodCacheBuilder.FromAssemblyOf<T>()`.
  If multiple modules generate the same interface, select exactly one; calling/entry assemblies have no special priority.
- In `appsettings.json`, write durations as `"00:05:00"` (a bare number is rejected), `Tags` as a list, and only known
  setting names; anything else fails startup.
- For a client from a NuGet package with an interface, use
  `[assembly: CacheInterface(typeof(IClient), nameof(IClient.Method), Duration = "...")]` instead of a wrapper.
- Without an attribute, use `ICacheManager.GetOrCreateAsync(key, factory, o => o.WithDuration(...))` with an explicit,
  prefixed key such as `$"order:{id}"`.
- Fix MethodCache's `MC` build diagnostics; each links to its explanation, and many have a code fix.
- In tests, register `NoOpCacheManager` (namespace `MethodCache.Extensibility`) after `AddMethodCache()` to turn
  method caching off. HTTP response storage remains independent; use its own options and administrator.
- To invalidate a `GetOrCreateAsync` entry that has a version, pass `CacheManagerExtensions.GetCacheKey(key, version)`
  to `InvalidateKeysAsync`.
- Use `UseRedis` or `UseSqlServer`; owned storage never falls back to the app's `IDistributedCache`. Configure only `DistributedCacheOptions.KeyPrefix` before building the container; use a new container for connection/prefix changes.
- `AddHttpCaching()` requires explicit `AddMethodCache()` in the same container, in either order. HTTP uses a separate `MaxHttpBytes` budget (100 MiB), or exclusively the configured native MethodCache Redis/SQL transport. Never use the app cache as a fallback. Send explicit Cookie headers, or set `KeepCookies=true` to preserve transport cookies and bypass response caching. Confirmed unsafe mutations still advance shared URL epochs.

## Namespaces

- `MethodCache`: `[Cache]`, `[CacheInvalidate]`, `[CacheInterface]`, `ICacheManager`, `ICacheAdministrator`,
  `IRuntimeCacheConfigurator`, `MethodCacheKeys`, `MethodCacheMemoryOptions`, `Any<T>`, `ICacheKeyProvider`.
- `MethodCache`: also `NoOpCacheManager` (sealed). Serializer, transport and atomic-store interfaces are internal.
- `Microsoft.Extensions.DependencyInjection`: `AddMethodCache`. Everything else is a method on its builder:
  `FromConfiguration`, `Distributed(...)`, `Telemetry(...)`, `UseRedis` (package `MethodCache.Redis`), `UseSqlServer`
  (package `MethodCache.SqlServer`) and `UseMessagePack` (package `MethodCache.Serialization.MessagePack`).

SQL backplane gap discovery remains non-blocking even for long transactions: only a separate non-waiting probe retires old missing identities. HTTP selection-rule changes retire a URL epoch once, preserving newer concurrent publications even if deletion fails after rotation. Gap failure coverage runs without infrastructure in the Core fault-injection suite, with linked SQL integration checks for real locking semantics.

The public ICacheManager contract is GetOrCreateAsync (a state-passing factory, like HybridCache), TryGetAsync,
InvalidateKeysAsync and InvalidateTagsAsync, all with the caller token last. CacheEntryOptions is immutable. Custom managers use GetCacheKey and CanCache helpers; generated
synchronous calls bypass a custom manager. Runtime configurators expose only Upsert/Remove/Clear/GetOverrides. They
reject unknown method ids and retire local policy generations. Declarative L2 namespaces are stable; runtime behavior
changes use private random namespaces. Never reintroduce cache callbacks, a factory context object, public policy pipelines,
key generators or backplanes. Recovery has independent L2 and publication counters and bounded queues.

Use named attribute settings (`Group = "name"`); `CacheKeyAttribute` and raw keys are unsupported. Omitted flags inherit, explicit false overrides. Duration grammar is shared by configuration and attributes (`30s`, `1.5h`, `[d.]hh:mm:ss`); ordinary durations must be positive. Disable fail-safe with `"off"` in attributes/configuration or `TimeSpan.Zero` in its builder. Format dynamic tag arguments with the canonical key-part rules; do not interpolate culture-dependent values manually.

Select JSON with `UseJson(context)` for Native AOT or `UseJson(options)` for explicit settings. Options are owned snapshots. `VerifyRoundTrip` defaults to true but does not prove nested polymorphic/private data preservation. SQL schema changes require explicit migration; do not use automatic DDL to reinterpret existing unversioned tables. Entry format/serializer versions change value namespaces; logical tombstones, wire-v1 invalidations and HTTP epochs remain format-independent.

Core owns container-scoped `IMeterFactory` instruments and its `MethodCache` ActivitySource. The OpenTelemetry package
only provides AddMethodCacheInstrumentation builder subscriptions. Never add metrics-provider interfaces, manager
wrappers or baggage propagators. Core MethodCacheTelemetryOptions controls explicit key/tag span opt-ins; hashes are
the default when keys are enabled. No keys/tags/URLs/user/tenant metric dimensions, copied HTTP attributes or exception
payloads in spans/logs. Preserve application ambient activity/baggage context. Histograms use seconds. Report local
method/HTTP entries, HTTP bytes, observed L2 entries and TotalWeight in configured units, never object-memory estimates.
Health checks honor failureStatus and independently report L2, backplane subscription and storage/publication queues.

- Generated keys and string policy ids start with the service assembly simple name in brackets, without its version. Closed generic argument identities include their assemblies. Build keys with `MethodCacheKeys`; use `TypePart(typeof(T))` when spelling a closed generic method name. Get exact policy ids from administration or prefer typed configuration expressions.
