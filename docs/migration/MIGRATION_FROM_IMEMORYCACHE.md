# Migration Guide: From IMemoryCache to MethodCache

This guide helps you migrate from ASP.NET Core's `IMemoryCache` to MethodCache for better performance, cleaner code, and enhanced features.

## Why Migrate?

| Feature | IMemoryCache | MethodCache |
|---------|--------------|-------------|
| **Performance** | Manual implementation | 8276x faster with cache hit (~145ns) |
| **Code Cleanliness** | Manual cache-aside pattern | Declarative attributes or fluent API |
| **Tag-based Invalidation** | Manual tracking required | Built-in support |
| **Distributed Caching** | Separate IDistributedCache | Unified L1/L2 hybrid caching |
| **Source Generation** | No | Yes - zero reflection |
| **Type Safety** | Limited | Full type safety with generics |

## Quick Comparison

### Before: IMemoryCache

```csharp
public class UserService
{
    private readonly IMemoryCache _cache;
    private readonly IUserRepository _repository;

    public UserService(IMemoryCache cache, IUserRepository repository)
    {
        _cache = cache;
        _repository = repository;
    }

    public async Task<User> GetUserAsync(int userId)
    {
        var cacheKey = $"user:{userId}";

        if (_cache.TryGetValue(cacheKey, out User cachedUser))
        {
            return cachedUser;
        }

        var user = await _repository.GetUserByIdAsync(userId);

        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(30))
            .SetPriority(CacheItemPriority.Normal);

        _cache.Set(cacheKey, user, cacheEntryOptions);

        return user;
    }

    public void InvalidateUser(int userId)
    {
        _cache.Remove($"user:{userId}");
    }

    public async Task UpdateUserAsync(int userId, UserUpdateDto update)
    {
        await _repository.UpdateUserAsync(userId, update);
        InvalidateUser(userId);
    }
}
```

### After: MethodCache

```csharp
public interface IUserService
{
    [Cache("users", Duration = "00:30:00", Tags = new[] { "users", "user:{userId}" })]
    Task<User> GetUserAsync(int userId);

    [CacheInvalidate(Tags = new[] { "users", "user:{userId}" })]
    Task UpdateUserAsync(int userId, UserUpdateDto update);
}

public class UserService : IUserService
{
    private readonly IUserRepository _repository;

    public UserService(IUserRepository repository)
    {
        _repository = repository;
    }

    public Task<User> GetUserAsync(int userId)
        => _repository.GetUserByIdAsync(userId);

    public Task UpdateUserAsync(int userId, UserUpdateDto update)
        => _repository.UpdateUserAsync(userId, update);
}
```

## Step-by-Step Migration

### Step 1: Install MethodCache

```bash
dotnet add package MethodCache.Core
dotnet add package MethodCache.SourceGenerator
```

### Step 2: Register MethodCache

Replace or supplement your existing `IMemoryCache` registration:

```csharp
// Before
builder.Services.AddMemoryCache();

// After - MethodCache includes its own memory cache
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10))
          .DefaultKeyGenerator<FastHashKeyGenerator>();
});
```

### Step 3: Choose Your Migration Strategy

#### Strategy A: Interface-Based (Recommended)

Extract an interface from your service and add `[Cache]` attributes:

```csharp
// 1. Extract interface
public interface IProductService
{
    Task<Product> GetProductAsync(int productId);
    Task UpdateProductAsync(int productId, ProductUpdateDto update);
}

// 2. Add attributes
public interface IProductService
{
    [Cache(Duration = "00:15:00", Tags = new[] { "products", "product:{productId}" })]
    Task<Product> GetProductAsync(int productId);

    [CacheInvalidate(Tags = new[] { "products", "product:{productId}" })]
    Task UpdateProductAsync(int productId, ProductUpdateDto update);
}

// 3. Simplify implementation - remove manual caching
public class ProductService : IProductService
{
    private readonly IProductRepository _repository;

    public ProductService(IProductRepository repository)
    {
        _repository = repository;
    }

    public Task<Product> GetProductAsync(int productId)
        => _repository.GetProductByIdAsync(productId);

    public Task UpdateProductAsync(int productId, ProductUpdateDto update)
        => _repository.UpdateProductAsync(productId, update);
}
```

#### Strategy B: Fluent API (For Existing Code)

Keep your existing code structure and wrap calls:

```csharp
public class OrderService
{
    private readonly ICacheManager _cache;
    private readonly IOrderRepository _repository;

    public OrderService(ICacheManager cache, IOrderRepository repository)
    {
        _cache = cache;
        _repository = repository;
    }

    public async Task<Order> GetOrderAsync(int orderId)
    {
        return await _cache.Cache(() => _repository.GetOrderByIdAsync(orderId))
            .WithDuration(TimeSpan.FromMinutes(30))
            .WithTags("orders", $"order:{orderId}")
            .ExecuteAsync();
    }

    public async Task UpdateOrderAsync(int orderId, OrderUpdateDto update)
    {
        await _repository.UpdateOrderAsync(orderId, update);
        await _cache.InvalidateByTagsAsync("orders", $"order:{orderId}");
    }
}
```

### Step 4: Migrate Cache Key Generation

#### Before: Manual String Concatenation

```csharp
var cacheKey = $"user:{userId}:profile:{profileType}";
```

#### After: Automatic Key Generation

```csharp
// MethodCache automatically generates keys from method name and parameters
[Cache]
Task<UserProfile> GetUserProfileAsync(int userId, ProfileType profileType);

// Or use fluent API with automatic key generation
await cache.Cache(() => service.GetUserProfileAsync(userId, profileType))
    .WithDuration(TimeSpan.FromMinutes(10))
    .ExecuteAsync();
```

### Step 5: Migrate Tag-Based Invalidation

#### Before: Manual Tag Tracking

```csharp
// You had to manually track and remove all related keys
_cache.Remove($"user:{userId}");
_cache.Remove($"user:{userId}:profile");
_cache.Remove($"user:{userId}:settings");
_cache.Remove($"user:{userId}:orders");
```

#### After: Tag-Based Invalidation

```csharp
// Invalidate all entries with matching tags in one call
await cacheManager.InvalidateByTagsAsync($"user:{userId}");

// Or use attribute
[CacheInvalidate(Tags = new[] { "user:{userId}" })]
Task UpdateUserAsync(int userId, UserUpdateDto update);
```

### Step 6: Migrate Expiration Policies

| IMemoryCache | MethodCache |
|-------------|-------------|
| `SetAbsoluteExpiration(TimeSpan)` | `Duration = "HH:MM:SS"` or `WithDuration(TimeSpan)` |
| `SetSlidingExpiration(TimeSpan)` | `WithSlidingExpiration(TimeSpan)` |
| `SetPriority(CacheItemPriority)` | Automatic (handled by L1/L2 layers) |
| `RegisterPostEvictionCallback()` | `OnMiss()` / `OnHit()` callbacks |

#### Before: IMemoryCache Options

```csharp
var options = new MemoryCacheEntryOptions()
    .SetAbsoluteExpiration(TimeSpan.FromMinutes(30))
    .SetSlidingExpiration(TimeSpan.FromMinutes(5))
    .RegisterPostEvictionCallback((key, value, reason, state) =>
    {
        Console.WriteLine($"Evicted: {key}");
    });

_cache.Set(key, value, options);
```

#### After: MethodCache Options

```csharp
// Attribute-based
[Cache(Duration = "00:30:00")]
Task<User> GetUserAsync(int userId);

// Fluent API
await cache.Cache(() => service.GetUserAsync(userId))
    .WithDuration(TimeSpan.FromMinutes(30))
    .WithSlidingExpiration(TimeSpan.FromMinutes(5))
    .OnMiss(ctx => Console.WriteLine($"Cache miss: {ctx.Key}"))
    .OnHit(ctx => Console.WriteLine($"Cache hit: {ctx.Key}"))
    .ExecuteAsync();
```

## Common Migration Patterns

### Pattern 1: Repository Caching

#### Before

```csharp
public class UserRepository : IUserRepository
{
    private readonly IMemoryCache _cache;
    private readonly DbContext _db;

    public async Task<User> GetUserByIdAsync(int userId)
    {
        if (_cache.TryGetValue($"user:{userId}", out User user))
            return user;

        user = await _db.Users.FindAsync(userId);
        _cache.Set($"user:{userId}", user, TimeSpan.FromMinutes(30));
        return user;
    }
}
```

#### After

```csharp
public interface IUserRepository
{
    [Cache(Duration = "00:30:00", Tags = new[] { "users", "user:{userId}" })]
    Task<User> GetUserByIdAsync(int userId);
}

public class UserRepository : IUserRepository
{
    private readonly DbContext _db;

    public Task<User> GetUserByIdAsync(int userId)
        => _db.Users.FindAsync(userId).AsTask();
}
```

### Pattern 2: External API Caching

#### Before

```csharp
public class WeatherService
{
    private readonly IMemoryCache _cache;
    private readonly HttpClient _http;

    public async Task<Weather> GetWeatherAsync(string city)
    {
        var key = $"weather:{city}";
        if (_cache.TryGetValue(key, out Weather weather))
            return weather;

        weather = await _http.GetFromJsonAsync<Weather>($"/api/weather/{city}");
        _cache.Set(key, weather, TimeSpan.FromMinutes(5));
        return weather;
    }
}
```

#### After

```csharp
public interface IWeatherService
{
    [Cache(Duration = "00:05:00", Tags = new[] { "weather", "weather:{city}" })]
    Task<Weather> GetWeatherAsync(string city);
}

public class WeatherService : IWeatherService
{
    private readonly HttpClient _http;

    public Task<Weather> GetWeatherAsync(string city)
        => _http.GetFromJsonAsync<Weather>($"/api/weather/{city}");
}
```

### Pattern 3: Bulk Operations

#### Before

```csharp
public async Task<List<User>> GetUsersAsync(List<int> userIds)
{
    var users = new List<User>();
    var missingIds = new List<int>();

    foreach (var id in userIds)
    {
        if (_cache.TryGetValue($"user:{id}", out User user))
            users.Add(user);
        else
            missingIds.Add(id);
    }

    if (missingIds.Any())
    {
        var fetchedUsers = await _repository.GetUsersByIdsAsync(missingIds);
        foreach (var user in fetchedUsers)
        {
            _cache.Set($"user:{user.Id}", user, TimeSpan.FromMinutes(30));
            users.Add(user);
        }
    }

    return users;
}
```

#### After

```csharp
// MethodCache handles individual caching automatically
public async Task<List<User>> GetUsersAsync(List<int> userIds)
{
    var tasks = userIds.Select(id => GetUserAsync(id));
    return (await Task.WhenAll(tasks)).ToList();
}

[Cache(Duration = "00:30:00", Tags = new[] { "users", "user:{userId}" })]
public Task<User> GetUserAsync(int userId)
    => _repository.GetUserByIdAsync(userId);
```

## Checklist

- [ ] Install MethodCache.Core and MethodCache.SourceGenerator
- [ ] Register MethodCache in DI (`AddMethodCache`)
- [ ] Extract interfaces for services with caching (if using attribute strategy)
- [ ] Add `[Cache]` attributes to read operations
- [ ] Add `[CacheInvalidate]` attributes to write operations
- [ ] Remove manual `IMemoryCache` injection and cache-aside code
- [ ] Replace manual key generation with automatic generation
- [ ] Migrate tag tracking to MethodCache tags
- [ ] Update expiration policies to MethodCache configuration
- [ ] Test cache behavior (hits, misses, invalidation)
- [ ] Monitor performance improvements

## Troubleshooting

### "Cache not working after migration"

**Check:**
1. Verify `services.AddMethodCache()` is called
2. Ensure interface is registered in DI
3. Confirm `Duration` is set (attribute or config)
4. Verify method is virtual or interface member

**Solution:**
```csharp
// Register service with interface
builder.Services.AddScoped<IUserService, UserService>();

// Add MethodCache
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10));
});
```

### "Getting stale data"

**Issue:** Cache not invalidating on updates

**Solution:** Add `[CacheInvalidate]` to write operations:
```csharp
[CacheInvalidate(Tags = new[] { "users", "user:{userId}" })]
Task UpdateUserAsync(int userId, UserUpdateDto update);
```

### "Cache keys conflict"

**Issue:** Different methods generating same cache key

**Solution:** Use more specific tags or key generators:
```csharp
[Cache(Tags = new[] { "user-profile" }, KeyGeneratorType = typeof(JsonKeyGenerator))]
Task<UserProfile> GetUserProfileAsync(int userId);

[Cache(Tags = new[] { "user-settings" }, KeyGeneratorType = typeof(JsonKeyGenerator))]
Task<UserSettings> GetUserSettingsAsync(int userId);
```

## Performance Comparison

| Scenario | IMemoryCache | MethodCache | Improvement |
|----------|-------------|-------------|-------------|
| Cache Hit | ~500ns | ~145ns | 3.4x faster |
| Cache Miss | ~1.5ms | ~1.3ms | Comparable |
| Tag Invalidation | Manual tracking | Built-in | Much easier |
| Code Lines (typical service) | ~40 lines | ~10 lines | 75% reduction |

## Next Steps

1. **Enable Analyzers**: Add `MethodCache.Analyzers` for compile-time validation
2. **Add Distributed Cache**: Install `MethodCache.Providers.Redis` for L2 caching
3. **Monitor Performance**: Use `ICacheMetricsProvider` for observability
4. **Optimize Key Generators**: Switch to `FastHashKeyGenerator` for production

## Resources

- [Configuration Guide](../user-guide/CONFIGURATION_GUIDE.md)
- [Fluent API Reference](../user-guide/FLUENT_API.md)
- [Third-Party Library Caching](../user-guide/THIRD_PARTY_CACHING.md)
- [GitHub Repository](https://github.com/eoniclabs/MethodCache)