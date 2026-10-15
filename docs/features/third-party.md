# Caching third-party code

MethodCache can cache calls to code you don't own, such as API clients, cloud SDKs or payment gateways, to cut API
costs and latency. Pick one of three approaches:

| Approach | Best for |
|----------|----------|
| `[assembly: CacheInterface]` | A client that ships an interface: cache chosen methods with one line, no wrapper |
| A wrapper interface with `[Cache]` | A client without an interface (a sealed class or static methods) |
| `ICacheManager.GetOrCreateAsync` | One or two calls, or keys you want to build yourself |

## The client's own interface

Name the interface and the methods to cache in an assembly attribute, anywhere in your project:

<!-- snippet: readme-cache-interface -->
```cs
[assembly: CacheInterface(typeof(IFxRatesClient), nameof(IFxRatesClient.GetRateAsync),
    Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]
```
<!-- endSnippet -->

<!-- snippet: readme-cache-interface-registration -->
```cs
builder.Services.AddSingleton<IFxRatesClient, FxRatesClient>();   // the vendor's client, registered as usual
builder.Services.AddMethodCache();
```
<!-- endSnippet -->

- The listed methods behave like methods with `[Cache]`: keys from the arguments, tag templates with the method's
  parameter names, zero-allocation hits on .NET 10. Methods you don't list always reach the client, so writes
  are never cached by accident.
- The attribute's settings (`Duration`, `Tags`, `Group`, `SlidingExpiration`, `RefreshAhead`, `Version`, `FailSafe`,
  `FailSafeTimeout`) apply to every listed method. Give one method its own settings like any cached method:

<!-- snippet: third-party-per-method -->
```cs
builder.Services.AddMethodCache(config => config.Method((IFxRatesClient c) => c.GetRateAsync(Any<string>.Value, Any<string>.Value))
    .Configure(o => o.WithDuration(TimeSpan.FromSeconds(30))));
```
<!-- endSnippet -->

- The method id in `appsettings.json` and the admin endpoints is the interface's full name, a dot and the method name,
  for example `FxVendor.IFxRatesClient.GetRateAsync`.
- List each interface once. It must be non-generic and come from another assembly; for your own interfaces, put
  `[Cache]` on the methods. A method with overloads can't be listed yet.
- The attribute can live in a class library, such as your infrastructure project. Select its generated module
  explicitly with `MethodCacheBuilder.FromAssemblyOf<T>()`, using a type declared in that assembly. This also
  bootstraps the module under Native AOT without relying on discovery of loaded assemblies.
- When multiple assemblies generate the same interface, select exactly one module. Calling and entry assemblies
  have no special precedence. An ambiguous registration throws `InvalidOperationException` naming the candidates.

<!-- snippet: third-party-module-selection -->
```cs
builder.Services.AddMethodCache(cache => cache.FromAssemblyOf<Rates>());
```
<!-- endSnippet -->

- Mistakes are build errors: [MC0011](../user-guide/DIAGNOSTICS.md#mc0011) (a type it can't cache, such as one with
  static abstract or non-public abstract members), [MC0012](../user-guide/DIAGNOSTICS.md#mc0012) (a method that is
  missing, overloaded, static, or one `[Cache]` would reject) and [MC0013](../user-guide/DIAGNOSTICS.md#mc0013) (an
  interface listed twice).

## A wrapper interface

Define an interface in your project with the calls you need, and put the settings on it:

<!-- snippet: third-party-wrapper -->
```cs
public interface IRates
{
    [Cache(Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]
    Task<decimal> GetAsync(string from, string to);
}

public sealed class Rates(FxRatesClient client) : IRates   // calls the vendor's concrete class
{
    public Task<decimal> GetAsync(string from, string to) => client.GetRateAsync(from, to);
}
```
<!-- endSnippet -->

<!-- snippet: third-party-wrapper-registration -->
```cs
builder.Services.AddSingleton<FxRatesClient>();
builder.Services.AddScoped<IRates, Rates>();
builder.Services.AddMethodCache();
```
<!-- endSnippet -->

Static methods and sealed classes work the same way: the wrapper calls them, and the interface carries the attributes.

## An explicit key

<!-- snippet: readme-explicit-key -->
```cs
var rate = await cache.GetOrCreateAsync(
    $"fx:{from}:{to}",
    async (ctx, ct) => await client.GetRateAsync(from, to),
    o => o.WithDuration(TimeSpan.FromMinutes(1)).WithTags("fx"));
```
<!-- endSnippet -->

See [Manual caching](manual-caching.md).

## Things to know

- **Leave writes uncached.** Payments, uploads and deletes must not be cached; mark wrapper methods that change data
  with `[CacheInvalidate(Tags = ...)]`.
- **Exceptions are not cached.** With [fail-safe](fail-safe.md) on, the previous value is served while the client
  fails.
- **Invalidate from a webhook** with `cache.InvalidateTagAsync("fx:EUR")` when the vendor tells you data changed.
- **Share results between instances** with a [distributed cache](distributed-cache.md) when every instance calls the
  same paid API.
- **Start with short durations** and lengthen them once you know how fresh the data must be.
