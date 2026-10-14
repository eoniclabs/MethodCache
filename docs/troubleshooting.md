# Troubleshooting

To see what is actually cached, list the entries with `ICacheAdministrator.ListEntries()` or `GET /methodcache/entries`,
and the effective policy of every method with `GetMethods()` or `GET /methodcache/methods` ([Monitoring](features/monitoring.md)).

## The method runs on every call

- **The attribute is on the class.** `[Cache]` is read from the interface only. The analyzer reports
  [MC0001](user-guide/DIAGNOSTICS.md#mc0001) and its code fix moves the attribute.
- **The service was registered after `AddMethodCache()`.** Call `AddMethodCache()` after your own registrations.
- **An open generic registration.** `AddScoped(typeof(IRepository<>), ...)` is not decorated; register the closed
  types ([Getting started](getting-started.md#3-register-it)).
- **The caller doesn't use the interface.** The cache is a decorator around the interface. Resolving or injecting the
  class itself, or calling the method from inside the class (`this.GetAsync(...)`), skips it.
- **The arguments differ between calls.** A `DateTime.Now`, a new `Guid` or a request object with a unique id in it
  makes every key different. List the keys to check ([Cache keys](features/keys.md)).
- **`StringBuilder`, `CultureInfo` or another framework type in an argument** is build error
  [MCG018](user-guide/DIAGNOSTICS.md#mcg018): its key would not follow its value. Pass a string or an id instead
  ([Cache keys](features/keys.md#framework-types)).
- **The result is not stored on purpose:** a `CacheWhen` predicate rejected it, the result is `null` with
  `CacheNullValues = false`, or the call threw. Exceptions are never cached.
- **A test registered `NoOpCacheManager`**, which never caches ([Testing](features/testing.md)).

## Old data after an update

- **The tags don't match.** `[CacheInvalidate]` removes only the tags it lists. Compare them with the cached methods'
  tags.
- **A tag template isn't filled in.** The name in braces must be a parameter of that method:
  [MCG009](user-guide/DIAGNOSTICS.md#mcg009) warns about unknown names. A property path such as `"{product.Id}"` is
  not a template and stays literal ([MCG015](user-guide/DIAGNOSTICS.md#mcg015)); pass the id as a parameter.
- **The update method threw.** Invalidation runs only after the method completes successfully.
- **The data changed outside the interface**, for example in another service or a database job. Invalidate from that
  code with `ICacheManager.InvalidateByTagsAsync` ([Invalidation](features/invalidation.md#from-code)).
- **You run several instances** with only the in-memory cache. Each instance has its own cache; add
  [Redis or SQL Server](features/distributed-cache.md) so invalidations reach all of them. With a custom
  `IDistributedCache`, also register an `IBackplane`.
- **Fail-safe returned an expired value** because the method failed ([Fail-safe](features/fail-safe.md)).

## Results stay out of the distributed cache

A method whose result type is `object`, an interface or an abstract class (`Task<IShape>`, `Task<List<object>>`) has
warning [MCG019](user-guide/DIAGNOSTICS.md#mcg019). The serializer can't tell which class to create when it reads
such a result back, so when a distributed cache is used the default round-trip check keeps these results in memory
only, and each instance loads its own. Return a concrete type, such as `Task<Circle>` or `Task<List<Product>>`.

## Keys look different than expected

List the keys with `ICacheAdministrator.ListEntries()` or `GET /methodcache/entries`, and build the key you expect with
`MethodCacheKeys.For(...)` to compare them. [Cache keys](features/keys.md) explains how each argument becomes part of
a key.

## A change in appsettings.json has no effect

- `AddMethodCacheFromConfiguration(builder.Configuration)` must be called.
- The key under `Services` must be the method id: the interface's full name, a dot and the method name. An unknown id
  is logged as a warning at startup. `GET /methodcache/methods` lists the ids.
- A runtime override for the method wins over the file ([Configuration](features/configuration.md)).
- An invalid value in a reloaded file is logged as an error, and the previous policies stay.

## Exceptions

| Exception | Cause |
|-----------|-------|
| `InvalidOperationException` when building a key | An argument type has only private state, so every value would get the same key. Implement `ICacheKeyProvider` ([Cache keys](features/keys.md#types-whose-state-is-private)) |
| `InvalidOperationException` at startup | `OnHit`, `OnMiss`, `WithMetrics` or `When` configured for a `[Cache]` method. They apply to `GetOrCreateAsync` only. Without a generic host, the first cached call throws |
| `InvalidOperationException` from `AddMethodCache` naming two assemblies | Two assemblies list the same interface in `[assembly: CacheInterface]`; pass the one whose policies apply ([Caching third-party code](features/third-party.md)) |
| `NotSupportedException` from `InvalidateByTagPatternAsync` | Pattern invalidation doesn't work with a distributed cache. Invalidate exact tags |
| `ArgumentOutOfRangeException` from `WithDuration` | Durations must be positive. An override can't turn caching off; invalidate instead |
| `InvalidOperationException` at startup naming a `MethodCache:` path | An invalid value, an unknown setting name, a single value for `Tags` or for a section that holds settings, or a time span without `:` in the `MethodCache` section |

## A caller changed a cached object, and others see the change

The cache hands the same instance to every caller. Return immutable types such as records, or copies
([Memory cache](features/memory-cache.md#cached-objects-are-shared)).

## Build errors and warnings

Every `MC` and `MCG` diagnostic, with what to do about it, is in [Build diagnostics](user-guide/DIAGNOSTICS.md).
