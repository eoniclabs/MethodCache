# Third-Party Library Caching Guide

MethodCache can cache calls to libraries you do not own, such as API clients, cloud SDKs or payment gateways, to cut API costs and latency. Typical candidates are expensive reads whose results can be a little stale.

Caching is generated for methods that carry a `[Cache]` attribute in your own code, so you cannot attach caching to a third-party interface through configuration alone. Use one of these two approaches:

| Approach | Best for |
|----------|----------|
| **Wrapper interface with `[Cache]`** | Several methods of one client, with policies you want to tune from appsettings.json or at runtime |
| **`ICacheManager.GetOrCreateAsync`** | One or two calls, or keys you want to build yourself |

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

- **Exceptions are not cached.** If the client throws, the exception propagates and the next call tries again. With `MemoryCacheOptions.EnableFailSafe`, the previous value is served instead while it is within `FailSafeMaxDuration`.
- **Keys contain arguments.** Generated keys include the method's arguments (hashed when they are not simple values). Avoid caching methods whose arguments are secrets, or pass a non-secret identifier instead.
- **Start with short durations** for a new integration, and lengthen them once you know how fresh the data must be.
- **Share results between instances** with a distributed cache (`AddMethodCacheRedis`, `AddMethodCacheSqlServer` or `AddMethodCacheDistributedCache`) when every instance calls the same paid API.
