[← MethodCache documentation](../README.md)

# Blazor

MethodCache works in Blazor apps without anything Blazor-specific to install: put `[Cache]` on an interface, register
it and call `AddMethodCache()`, as in [Getting started](../getting-started.md). Where the code runs decides what is
cached and who shares it:

| Where the code runs | Who shares a cached result | Distributed cache |
|---|---|---|
| On the server: Blazor Server, `InteractiveServer`, static server rendering and prerendering | Every user and circuit of the app instance | Redis, SQL Server or PostgreSQL, as in any ASP.NET Core app |
| In the browser: Blazor WebAssembly, `InteractiveWebAssembly` | One browser tab, until it is reloaded or closed | None: the browser can't reach them |

`InteractiveAuto` runs a component on the server first and in the browser later, so it uses both caches. They are
separate: an invalidation in one doesn't reach the other.

CI publishes a Blazor WebAssembly app on every change and runs it in a headless browser: hits, one load for concurrent
callers, tag invalidation, expiry, refresh-ahead and clearing the cache.

## On the server

A Blazor Server app, and every component the server renders in a Blazor Web App, is an ASP.NET Core app, and the rest
of these docs apply as they are. Two things differ from a web API:

- A scoped service lives as long as the user's circuit, not one request. The cached results are still shared by
  every user, whatever the lifetime of the service.
- In an interactive component there is no `HttpContext`. The signed-in user comes from `AuthenticationStateProvider`.

A cached implementation must not read the signed-in user, because the key holds only the method's arguments and the
next user would get the result. Read the user outside the cached service and pass the user's id as an argument:

<!-- snippet: blazor-user-as-argument -->
```cs
public interface IOrders
{
    [Cache(Duration = "5m")]
    Task<IReadOnlyList<Order>> ForUserAsync(string userId);
}

// Not cached: reads the signed-in user of the circuit and passes the id on, so it is part of the key
public sealed class MyOrders(AuthenticationStateProvider auth, IOrders orders)
{
    public async Task<IReadOnlyList<Order>> GetAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        var userId = state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Nobody is signed in.");
        return await orders.ForUserAsync(userId);
    }
}
```
<!-- endSnippet -->

[MC0026](../user-guide/DIAGNOSTICS.md#mc0026) warns when a cached implementation takes `AuthenticationStateProvider`,
`ProtectedSessionStorage` or `ProtectedLocalStorage`, as it does for `IHttpContextAccessor` and `ClaimsPrincipal`.

## In the browser

Blazor WebAssembly runs the same `MethodCache` package in the browser, in memory only. Caching there saves calls to
your server: cache the client that calls your API, and each result is fetched once per tab until it expires or is
invalidated.

<!-- snippet: blazor-wasm-api-client -->
```cs
public interface ICatalog
{
    [Cache(Duration = "10m")]
    Task<IReadOnlyList<Product>> SearchAsync(string text);
}

// In the WebAssembly project: each search reaches the server once per browser tab within ten minutes
public sealed class CatalogClient(HttpClient http) : ICatalog
{
    public async Task<IReadOnlyList<Product>> SearchAsync(string text)
        => await http.GetFromJsonAsync<List<Product>>($"api/products?text={Uri.EscapeDataString(text)}") ?? [];
}
```
<!-- endSnippet -->

<!-- snippet: blazor-wasm-registration -->
```cs
// Program.cs of the WebAssembly project
services.AddScoped<ICatalog, CatalogClient>();
services.AddMethodCache();   // memory only: the browser can't reach Redis, SQL Server or PostgreSQL
```
<!-- endSnippet -->

- Don't add `MethodCache.Redis`, `MethodCache.SqlServer` or `MethodCache.PostgreSql` to the WebAssembly project.
  Cache on the server as well when every user should share a result.
- The cache is gone when the tab is reloaded or closed. Two tabs have a cache each, and an invalidation in one tab
  doesn't reach the other.
- Everything cached in the tab belongs to the user who is signed in there. Clear the cache before that user signs
  out, so the next user of the tab doesn't see those results:

<!-- snippet: blazor-wasm-sign-out -->
```cs
// Before signing out in the browser, so the next user of this tab doesn't see the previous user's results
public static async Task SignOutAsync(ICacheAdministrator cache)
{
    await cache.ClearAsync();
    // ... then sign out as usual
}
```
<!-- endSnippet -->

- Publishing trims the app, MethodCache included, and MethodCache needs nothing extra for it. With the trim analyzer
  on (`<EnableTrimAnalyzer>true</EnableTrimAnalyzer>`), a cached method whose arguments need reflection for the key
  gets a warning (IL2026) on its generated decorator. Give it the arguments listed
  [under Native AOT](native-aot.md#keys), above all with `TrimMode` set to `full`, which can trim away the members of
  other argument types.

## Prerendering and `InteractiveAuto`

A prerendered component runs twice: once on the server to produce the first HTML, and again when it becomes
interactive, and a component that loads data in `OnInitializedAsync` loads it both times. With `InteractiveServer`
both runs are on the server, so the second is a cache hit when the cached method gets the same arguments. With
`InteractiveWebAssembly` or `InteractiveAuto` the second run is in the browser, whose cache is new, so the call is made
again. Keep the prerendered data with Blazor's `[PersistentState]` on the component's property, and the browser
doesn't load it a second time.

With `InteractiveAuto` the interface usually has two implementations: one on the server that reads the database, and
one in the client project that calls your API. Put `[Cache]` and `[CacheInvalidate]` on the interface they share, and
each side caches its own calls. A write clears the cache of the side that makes it: called in the browser, it clears
the browser's copy, and your API, which runs the server's implementation, clears the server's. Other tabs and other
users' browsers keep their copy until it expires, so give results that change often a short `Duration`.
