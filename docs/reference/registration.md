[← MethodCache documentation](../README.md)

# Registration reference

The exact rules for how `AddMethodCache()` finds and wraps cached services, for advanced users and coding agents.
Start with [Getting started](../getting-started.md#3-register-it).

## Order

- `AddMethodCache()` wraps every registration of an interface that has cached methods in its generated decorator, and
  keeps the lifetime and implementation you registered. Register cached services before it.
- A cached service registered after `AddMethodCache()` fails when the host starts, or, without a host, when its
  decorator is first created. Other services can be registered later.
- Calling `AddMethodCache()` again wraps the services added since, and leaves the existing decorators as they are.
- The container disposes the implementations it creates, and leaves instances you registered yourself open, as
  without MethodCache. A decorator is disposable only when the interface is.

## Services you didn't register

- A cached interface without a registration is registered only with `AutoRegister(lifetime)`:
  `AddMethodCache(mc => mc.AutoRegister(ServiceLifetime.Scoped))`.
- It needs exactly one accessible class that implements the interface in the generated module. None, or several, is
  an error that names the service, the module and the candidates.
- Registrations you made yourself always keep their implementation and lifetime.

## Several assemblies

- Every assembly that references `MethodCache` generates decorators for its own cached interfaces, and all of them are
  used.
- When two assemblies generate a decorator for the same interface, choose the one that wins with
  `FromAssemblyOf<SomeTypeInThatAssembly>()` (or `FromAssembly(...)`). That only settles the tie: the services the other
  assemblies generate stay cached. There is no preference by entry assembly or name, under JIT and Native AOT alike.

## Keyed registrations

- `AddKeyedScoped<IProductService, ProductService>("primary")` is cached under the same key. An interface registered
  only keyed is not also registered without one.
- The service key is part of the cache keys, so each keyed registration caches its own results. It must be a string,
  enum, `bool`, `char`, integer or `Guid` ([Keys](../features/keys.md#things-to-know)).
- A catch-all registration with `KeyedService.AnyKey` is rejected, since its implementation is created for whichever key
  is requested; register concrete keys instead.
- A constructor parameter marked `[ServiceKey]` receives your key, as without MethodCache.

## Several registrations of one interface

- `AddScoped<IPriceProvider, StorePrices>()` and `AddScoped<IPriceProvider, PartnerPrices>()`, resolved as
  `IEnumerable<IPriceProvider>`, each cache their own results: the implementation type is part of their keys.
- Two registrations of the same implementation type share their results.
- `IMethodCacheKeys.For` and `Tag` use the last registration, the one `GetService` returns. When that is a scoped or
  transient factory registration among several, they throw, since the factory may create another type in each scope;
  register the implementation type, give it a service key of its own, or invalidate by tag.

## Generic interfaces

- `IRepository<User>` is cached for every closed type a class in your project implements, such as
  `UserRepository : IRepository<User>`.
- An open generic registration, `AddScoped(typeof(IRepository<>), typeof(Repository<>))`, and a closed type without a
  generated decorator give an error that names the binding, at startup or on first use without a host. Register the
  closed types instead.

## Factory registrations

A factory registration is cached like any other: register it before `AddMethodCache()`, and the decorator wraps what
your factory returns, with the lifetime you chose.

## Your own cache manager

- `services.AddSingleton<ICacheManager, MyManager>()` after `AddMethodCache()` makes the decorators use your manager.
  `NoOpCacheManager` turns caching off ([Testing](../features/testing.md)).
- A keyed `ICacheManager` registration is independent of the default one, and doesn't stop `AddMethodCache()` from
  registering its own.
- Your manager stores an entry under `MethodCacheKeys.StoredKey(key, version)` of the key and version it gets, so a key
  and a version name the same entry in every operation. Keys from generated decorators already carry the method's
  version, with `version` set to `null`.
- With your own manager, which has only async operations, synchronous `[Cache]` methods aren't cached (a warning is
  logged once per method), and a synchronous `[CacheInvalidate]` method fails with `NotSupportedException`.
