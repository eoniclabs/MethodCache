# Troubleshooting

To see what is actually cached, list the entries with `ICacheAdministrator.ListEntries()` or `GET /methodcache/entries`,
and the effective policy of every method with `GetMethods()` or `GET /methodcache/methods` ([Monitoring](features/monitoring.md)).

## The method runs on every call

- **The attribute is on the class.** `[Cache]` is read from the interface only. The analyzer reports
  [MC0001](user-guide/DIAGNOSTICS.md#mc0001) and its code fix moves the attribute.
- **A late or unsupported registration raises a named binding error.** Register cacheable services before
  `AddMethodCache()`. Open generic registrations such as `AddScoped(typeof(IRepository<>), ...)` are rejected;
  register generated closed types ([Getting started](getting-started.md#3-register-it)).
- **The caller doesn't use the interface.** The cache is a decorator around the interface. Resolving or injecting the
  class itself, or calling the method from inside the class (`this.GetAsync(...)`), skips it.
- **The arguments differ between calls.** A `DateTime.Now`, a new `Guid` or a request object with a unique id in it
  makes every key different. List the keys to check ([Cache keys](features/keys.md)).
- **`StringBuilder`, `CultureInfo` or another framework type in an argument** is build error
  [MC0019](user-guide/DIAGNOSTICS.md#mc0019): its key would not follow its value. Pass a string or an id instead
  ([Cache keys](features/keys.md#framework-types)).
- **The result is not stored on purpose:** a `CacheWhen` predicate rejected it, the result is `null` with
  `CacheNullValues = false`, or the call threw. Exceptions are never cached.
- **A test registered `NoOpCacheManager`**, which never caches ([Testing](features/testing.md)).

## Old data after an update

- **The tags don't match.** `[CacheInvalidate]` removes only the tags it lists. Compare them with the cached methods'
  tags.
- **A tag template isn't filled in.** The name in braces must be a parameter of that method:
  [MC0010](user-guide/DIAGNOSTICS.md#mc0010) warns about unknown names. A property path such as `"{product.Id}"` is
  not a template and stays literal ([MC0016](user-guide/DIAGNOSTICS.md#mc0016)); pass the id as a parameter.
- **The update method threw.** Invalidation runs only after the method completes successfully.
- **The data changed outside the interface**, for example in another service or a database job. Invalidate from that
  code with `ICacheManager.InvalidateTagsAsync` ([Invalidation](features/invalidation.md#from-code)).
- **You run several instances** with only the in-memory cache. Each instance has its own cache; add
  [Redis or SQL Server](features/distributed-cache.md) with its owned backplane so invalidations reach all of them.
  The application's `IDistributedCache` is not a MethodCache provider.
- **Fail-safe returned an expired value** because the method failed ([Fail-safe](features/fail-safe.md)).

## Results stay out of the distributed cache

A method whose result type is `object`, an interface or an abstract class (`Task<IShape>`, `Task<List<object>>`) has
warning [MC0020](user-guide/DIAGNOSTICS.md#mc0020). The serializer can't tell which class to create when it reads
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
| `InvalidOperationException` from `AddMethodCache` naming two assemblies | Multiple generated modules contain the same interface; select exactly one with `FromAssemblyOf<T>()` ([Caching third-party code](features/third-party.md)) |
| `ArgumentOutOfRangeException` from `WithDuration` | Durations must be positive. Use `WithEnabled(false)` to disable caching, including in runtime overrides |
| `InvalidOperationException` at startup naming a `MethodCache:` path | An invalid value, an unknown setting name, a single value for `Tags`, `Metadata` or a section that holds settings, nested settings under a single setting, or an invalid duration in the `MethodCache` section; positive invariant unit durations such as `30s` are supported |

## A caller changed a cached object, and others see the change

The cache hands the same instance to every caller. Return immutable types such as records, or copies
([Memory cache](features/memory-cache.md#cached-objects-are-shared)).

## Build errors and warnings

Every current `MC` diagnostic and the historical `MCG` migration mapping, with what to do about it, is in [Build diagnostics](user-guide/DIAGNOSTICS.md).
