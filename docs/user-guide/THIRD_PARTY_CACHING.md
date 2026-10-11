# Third-Party Library Caching Guide

MethodCache can cache calls to libraries you do not own, such as API clients, cloud SDKs or payment gateways, to cut API costs and latency. Typical candidates are expensive reads whose results can be a little stale.

Pick one of three approaches:

| Approach | Best for |
|----------|----------|
| **`[assembly: CacheInterface]`** | A client that ships an interface: cache chosen methods of it with one line, no wrapper |
| **Wrapper interface with `[Cache]`** | A client without an interface (a sealed class or static methods), or per-method attributes you want in code |
| **`ICacheManager.GetOrCreateAsync`** | One or two calls, or keys you want to build yourself |

## Caching the Client's Own Interface

Name the interface and the methods to cache in an assembly attribute, anywhere in your project:

```csharp
[assembly: CacheInterface(typeof(IWeatherClient),
    nameof(IWeatherClient.GetCurrentAsync), nameof(IWeatherClient.GetForecastAsync),
    Duration = "00:05:00", Tags = new[] { "weather", "city:{city}" })]
```

```csharp
builder.Services.AddSingleton<IWeatherClient, WeatherClient>(); // the vendor's client, registered as usual
builder.Services.AddMethodCache();                               // wraps IWeatherClient too
```

The listed methods behave exactly like methods with `[Cache]`: keys are built from the arguments, tag templates such as
`city:{city}` use the method's parameter names, and hits on .NET 9 and later allocate nothing. Every other method of
the interface calls the client directly, so writes are never cached by accident.

- The settings on the attribute (`Duration`, `Tags`, `Group`, `SlidingExpiration`, `RefreshAhead`, `Version`) apply
  to every listed method. Give one method different settings with the fluent API, `appsettings.json` or runtime
  overrides, as for any cached method:

  ```csharp
  builder.Services.AddMethodCache(config => config
      .ForService<IWeatherClient>()
      .Method(c => c.GetForecastAsync(default!, default))
      .Configure(o => o.WithDuration(TimeSpan.FromHours(1))));
  ```

- The method id in `appsettings.json` and the admin endpoints is the interface's full name, a dot and the method name,
  for example `Vendor.Weather.IWeatherClient.GetForecastAsync`.
- List one attribute per interface. The interface must be non-generic and come from another assembly; for your own
  interfaces, put `[Cache]` on the methods. A method with overloads can't be listed yet.
- Mistakes are build errors: MCG010 (a type it can't cache), MCG011 (a listed method that is missing, overloaded or
  returns nothing to cache) and MCG012 (an interface listed twice).

## Wrapper Interface with Attributes

Define an interface in your project with the calls you need, and put the cache settings on it:

```csharp
public interface IWeatherService
{
    [Cache(Duration = "00:05:00", Tags = new[] { "weather", "city:{city}" })]
    Task<CurrentWeather> GetCurrentAsync(string city);

    [Cache(Duration = "01:00:00", Tags = new[] { "weather", "city:{city}" })]
    Task<Forecast> GetForecastAsync(string city, int days);
}

public sealed class WeatherService : IWeatherService
{
    private readonly IWeatherClient _client; // the third-party client

    public WeatherService(IWeatherClient client) => _client = client;

    public Task<CurrentWeather> GetCurrentAsync(string city) => _client.GetCurrentAsync(city);
    public Task<Forecast> GetForecastAsync(string city, int days) => _client.GetForecastAsync(city, days);
}
```

```csharp
builder.Services.AddSingleton<IWeatherClient, WeatherClient>();
builder.Services.AddScoped<IWeatherService, WeatherService>();
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheFromConfiguration(builder.Configuration); // optional
```

Static methods and sealed classes from a library work the same way: the wrapper calls them, and the interface carries the attributes.

Because the methods are cached through `[Cache]`, their policies can be changed without a deploy, in appsettings.json (method id: your interface's full name, a dot, and the method name) or with `IRuntimeCacheConfigurator`:

```json
{
  "MethodCache": {
    "Services": {
      "MyApp.External.IWeatherService.GetCurrentAsync": {
        "Duration": "00:02:00",
        "Tags": ["weather", "external-api"]
      }
    }
  }
}
```

See the [Configuration Guide](CONFIGURATION_GUIDE.md) for the settings, priorities and runtime overrides.

Leave writes and other non-idempotent calls (creating payments, uploads, deletes) without `[Cache]`, or mark them `[CacheInvalidate(Tags = ...)]` so they remove the entries they make stale.

## Manual Caching with GetOrCreateAsync

Wrap the call and pass an explicit key:

```csharp
public sealed class WeatherFacade
{
    private readonly IWeatherClient _client;
    private readonly ICacheManager _cache;

    public WeatherFacade(IWeatherClient client, ICacheManager cache)
    {
        _client = client;
        _cache = cache;
    }

    public ValueTask<CurrentWeather> GetCurrentAsync(string city) =>
        _cache.GetOrCreateAsync(
            $"weather:{city}",
            async (ctx, ct) => await _client.GetCurrentAsync(city),
            o => o.WithDuration(TimeSpan.FromMinutes(5))
                  .WithTags("weather", $"city:{city}"));
}
```

You choose the key, so two calls share an entry only when their keys are equal. Policies set here live in code; configuration files and runtime overrides do not apply to them. See the [Fluent API Guide](FLUENT_API.md) for bulk and streaming helpers.

## Invalidation

Invalidate by tag when you know the data changed, for example from a webhook:

```csharp
await cacheManager.InvalidateByTagsAsync("city:oslo");
```

The admin endpoints in `MethodCache.AspNetCore` (`POST /methodcache/invalidate` with `{ "tags": [...] }`) do the same over HTTP.

## Things to Keep in Mind

- **Exceptions are not cached.** If the client throws, the exception propagates and the next call tries again. With fail-safe on (`config.FailSafe(...)` or `FailSafe = "01:00:00"` on the attribute), the previous value is served instead while it is within the fail-safe duration.
- **Keys contain arguments.** Generated keys include the method's arguments (hashed when they are not simple values). Avoid caching methods whose arguments are secrets, or pass a non-secret identifier instead.
- **Start with short durations** for a new integration, and lengthen them once you know how fresh the data must be.
- **Share results between instances** with a distributed cache (`AddMethodCacheRedis`, `AddMethodCacheSqlServer` or `AddMethodCacheDistributedCache`) when every instance calls the same paid API.
