[← MethodCache documentation](../README.md)

# Testing

The implementation of a cached interface is a plain class: create it directly in unit tests and no caching is
involved.

## Testing with caching

To test the caching itself, such as invalidation tags, build a service provider the way the application does:

<!-- snippet: testing-with-caching -->
```cs
var services = new ServiceCollection();
services.AddSingleton<Database>();
services.AddScoped<IProductService, ProductService>();
services.AddMethodCache();
var provider = services.BuildServiceProvider();

// Resolved through the generated decorator, exactly as in the application
var products = provider.CreateScope().ServiceProvider.GetRequiredService<IProductService>();
```
<!-- endSnippet -->

`ICacheAdministrator` from the same provider lists the entries and their keys, which helps when a test needs to check
what was cached ([Monitoring](monitoring.md)).

## Replacing a cached service with a fake

A `WebApplicationFactory` that swaps a cached service for a fake registers the fake after the application's
`AddMethodCache()`. Call `AddMethodCache()` again after it, so the fake gets a decorator like the service it replaces:

<!-- snippet: testing-replace-cached-service -->
```cs
// In a WebApplicationFactory: ConfigureTestServices runs after the app's AddMethodCache()
builder.ConfigureTestServices(services =>
{
    services.AddScoped<IProductService, FakeProductService>();
    services.AddMethodCache();   // again, so the fake gets a decorator like the service it replaces
});
```
<!-- endSnippet -->

Without the second call, the first cached service the test resolves fails with an error that says to register the
service before `AddMethodCache()` or call it again. The fake's results are cached like the real service's; when a test
changes what the fake returns, also register `NoOpCacheManager` ([Turning method caching off](#turning-method-caching-off)).

## Counting loads and invalidations

The `MethodCache.Testing` package records what the cache does. Add it to the test project at the same version as
`MethodCache`, and call `AddMethodCacheTesting()`: it registers a `CacheRecorder` and a `FakeTimeProvider` as the
container's clock.

<!-- snippet: testing-recorder -->
```cs
var services = new ServiceCollection();
services.AddSingleton<Database>();
services.AddScoped<IProductService, ProductService>();
services.AddMethodCache();
services.AddMethodCacheTesting();   // a fake clock and a recorder
await using var provider = services.BuildServiceProvider();
var recorder = provider.GetRequiredService<CacheRecorder>();
var time = provider.GetRequiredService<FakeTimeProvider>();
var products = provider.CreateScope().ServiceProvider.GetRequiredService<IProductService>();

await products.GetAsync(42);
await products.GetAsync(42);
Assert.Equal(1, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));   // the second call was a hit

await products.UpdateAsync(42, new Product(42, "Green tea", 5m));
Assert.Contains("product:42", recorder.InvalidatedTags);
await products.GetAsync(42);
Assert.Equal(2, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));

time.Advance(TimeSpan.FromMinutes(11));   // past the Duration of 10 minutes
await products.GetAsync(42);
Assert.Equal(3, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));
```
<!-- endSnippet -->

`Loads` counts how many times one call, with these arguments, ran your implementation: its misses, refreshes and
reloads after an invalidation. `InvalidatedTags` and `InvalidatedKeys` list what was invalidated, in order, by
`[CacheInvalidate]` methods, `ICacheManager` calls and `MethodCache.EntityFrameworkCore`. `Clear()` forgets what the
test's setup recorded. The recorder sees the built-in cache manager only: with `NoOpCacheManager` or your own
`ICacheManager`, it records nothing.

## When a test writes to the database directly

A test that changes data without going through a `[CacheInvalidate]` method, such as seeding rows or calling a
repository, leaves the cached results as they were. Clear the cache after the write, or invalidate what it changed:

<!-- snippet: testing-direct-write -->
```cs
await database.SaveAsync(changed);   // the test changes the data behind the cache's back

// Remove everything cached...
await provider.GetRequiredService<ICacheAdministrator>().ClearAsync();

// ...or only what the write changed
var cache = provider.GetRequiredService<ICacheManager>();
var keys = provider.GetRequiredService<IMethodCacheKeys>();
await cache.InvalidateTagAsync(keys.Tag<IProductService>(p => p.GetAsync(changed.Id, default), "product:{id}"));
```
<!-- endSnippet -->

`ClearAsync` also removes the entries from a distributed cache and the other instances, so use a cache of the test's
own (a new service provider, or its own `KeyPrefix`) when tests run in parallel against a shared Redis or database.

## Proving that a result is cached

A test that changes a row and reads it again proves caching only when the change doesn't invalidate. With
`MethodCache.EntityFrameworkCore`, a save through the connected context invalidates, so the second read is fresh
whether or not it is cached. Count the runs of your implementation with `recorder.Loads` instead, as above. To test
the stale value itself, change the row past MethodCache: with SQL, or through a context type that isn't connected to
MethodCache, over the same database. With EF Core's in-memory provider, give both contexts the same
`InMemoryDatabaseRoot`.

## Every cached service resolves

A service registered after `AddMethodCache`, or one with a dependency the container lacks, fails when the application
starts or on its first call. `CachedServices.ProblemsAsync` finds it in a test: it resolves each registered service with
cached methods, in a scope of its own, and checks that it is the generated caching decorator:

<!-- snippet: testing-cached-services -->
```cs
// Every service with cached methods resolves, and to its generated caching decorator
Assert.Empty(await CachedServices.ProblemsAsync(provider));
```
<!-- endSnippet -->

Each problem names the service, its key for a keyed registration, and what is wrong: registered after
`AddMethodCache`, an exception when it is resolved, or an instance that isn't the decorator, so its calls aren't
cached. Build the provider as the application does, with its own registrations, so the test resolves what the
application resolves. A service registered with an open generic type, such as `typeof(IRepository<>)`, is skipped.
When the provider has no service with cached methods, such as the wrong provider, it throws
`InvalidOperationException` instead of returning an empty list, so the test can't pass without checking anything.

When resolving the services needs a database, a key vault or other infrastructure the test doesn't have,
`CachedServices.RegistrationProblems` checks the registrations only and resolves nothing:

<!-- snippet: testing-cached-service-registrations -->
```cs
// Every service with cached methods is registered before AddMethodCache; resolves none of them
Assert.Empty(CachedServices.RegistrationProblems(provider));
```
<!-- endSnippet -->

It finds a service registered after `AddMethodCache`, the most common mistake, but not a dependency the container
lacks. Both methods take the provider the application builds, so whatever its registration code does still runs: a
client that connects to its database while it is registered needs that database in the test too.

Neither method can see a service that is never registered: it has no registration to check. `CachedServices.Registered`
lists the services with cached methods that the provider registers, each once and ordered by full name, so a test
can compare it with the services the application should have:

<!-- snippet: testing-cached-services-registered -->
```cs
// The application registers exactly these services with cached methods, none forgotten
Assert.Equal([typeof(IProductService)], CachedServices.Registered(provider));
```
<!-- endSnippet -->

What a host already checks: when a host built with `WebApplicationFactory` or `Host.CreateApplicationBuilder` starts,
`AddMethodCache` fails it for a service registered after `AddMethodCache`, so a test that starts the host finds that
mistake without these methods. `ProblemsAsync` adds what starting the host doesn't check: it resolves every cached
service, keyed ones too, so a missing dependency fails the test, and it works on a provider built without a host.

## Every tag has an invalidation

A tag that nothing invalidates keeps its entries until they expire. `TagCoverage.Uninvalidated` lists the tags of the
cached methods, from attributes, startup code and configuration, that nothing invalidates. It counts the
`[CacheInvalidate]` methods of the registered services, the entity mapping of
[`MethodCache.EntityFrameworkCore`](entity-framework-core.md), and the invalidations in the projects the test project
references:

<!-- snippet: testing-tag-coverage -->
```cs
// Every tag of a cached method has a [CacheInvalidate] method, an EF Core mapping or an invalidation in a referenced project
Assert.Empty(TagCoverage.Uninvalidated(provider));
```
<!-- endSnippet -->

When MethodCache builds a project, it writes the tags the project invalidates into its assembly: `[CacheInvalidate]`
tags, and the tags its code passes to `InvalidateTagAsync`, `InvalidateTagsAsync`, `InvalidateAfterCommit`, the entity
mapping or a helper marked `[InvalidatesCache]`, as constants, interpolated strings or `Tag(...)` calls. `TagCoverage`
reads them from the assemblies next to the tests, so a tag the API caches and a worker invalidates is covered when the
test project references the worker. The test project's own invalidations don't count.

A tag marked `InvalidatedElsewhere` ([In another application](invalidation.md#in-another-application)) is covered only
when a referenced project invalidates it. Otherwise it is reported, with `MarkedInvalidatedElsewhere` set, so the mark
can't hide a tag that nothing invalidates any more. Reference the project that invalidates it, or, when it is
invalidated outside the solution, such as by another team's application or a database trigger, name it in
`alsoInvalidatedBy`.

A tag marked `InvalidatedManually` ([Cleared by hand](invalidation.md#cleared-by-hand)) is covered without a check:
an operator clears it, so there is no invalidation to find. Mark it on `[Cache]` rather than listing it in
`alsoInvalidatedBy`, so analyzer MC0028 and the test read the same mark.

Name the tags your code invalidates in ways no project shows, such as a tag built in a variable:
`TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: ["price:{provider}:{model}"])`. A placeholder matches any
value, so `"product:{id}"` covers `"product:{Id}"` and `"product:42"`.

Analyzer [MC0028](../user-guide/DIAGNOSTICS.md#mc0028) checks the same within one project while you build; this
check sees the whole application, including the tags in configuration.

## A load for another tenant fails

A cached method that takes the tenant as a parameter and checks it against the request's tenant with a
`[ChecksRequestState]` method ([MC0026](../user-guide/DIAGNOSTICS.md#mc0026)) should fail for another tenant's id.
Test it with the request set to one tenant and a call for another:

<!-- snippet: testing-other-tenant -->
```cs
await using var scope = provider.CreateAsyncScope();
scope.ServiceProvider.GetRequiredService<RequestTenant>().Id = tenantA;   // the request is tenant A's
var projects = scope.ServiceProvider.GetRequiredService<IProjects>();

// A load for tenant B fails in the check, so nothing is cached under B's key
await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projects.ListAsync(tenantB));
```
<!-- endSnippet -->

The check runs when the method loads a value, not on a hit, so this test proves that nothing wrong is cached under
another tenant's key. That a caller takes the id from the request is the caller's part; test it where the id is taken,
such as in the endpoint.

## Turning method caching off

Register `NoOpCacheManager` after `AddMethodCache()` and every call reaches your implementation:

<!-- snippet: testing-without-caching -->
```cs
var services = new ServiceCollection();
services.AddSingleton<Database>();
services.AddScoped<IProductService, ProductService>();
services.AddMethodCache();
services.AddSingleton<ICacheManager, NoOpCacheManager>();   // after AddMethodCache: every call reaches ProductService
```
<!-- endSnippet -->

For code that uses [manual caching](manual-caching.md), pass a `NoOpCacheManager` directly, or mock the four
operations on `ICacheManager` (the short forms are extension methods that call them):

<!-- snippet: manual-testing -->
```cs
ICacheManager cache = new NoOpCacheManager();   // never caches: the factory runs on every call
var reader = new OrderReader(cache, repository);
```
<!-- endSnippet -->

`NoOpCacheManager` is in the `MethodCache` namespace. It turns off method caching only; responses of an `HttpClient`
with `AddHttpCaching` are still cached.

## Time

> [!TIP]
> The cache reads the time from the `TimeProvider` registered in the container, if there is one. Register a fake
> `TimeProvider`, such as the `FakeTimeProvider` that `AddMethodCacheTesting()` registers, to test expiration without
> waiting.
> This includes the background cleanup of the memory cache and, with `MethodCache.HttpCaching`, response freshness,
> eviction and the background revalidation timeout.

