# Simplified Tier 2 API Proposal

Since you have no users yet, we can make breaking changes to dramatically simplify the API!

## Current Tier 2 (Still Verbose)
```csharp
var user = await cache.GetOrCreateAsync(
    methodName: nameof(userRepo.GetUserAsync),
    args: new object[] { userId },
    factory: (ctx, ct) => userRepo.GetUserAsync(userId),
    configure: opts => opts.WithDuration(TimeSpan.FromHours(1))
);
```

## Simplified Option 1: CallerMemberName + Params
```csharp
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Func<ValueTask<T>> factory,
    Action<CacheEntryOptions.Builder>? configure = null,
    [CallerMemberName] string? callerMethod = null,
    params object[] args)

// Usage:
var user = await cache.GetOrCreateAsync(
    () => userRepo.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1)),
    args: userId  // or multiple: args: new object[] { userId, includeProfile }
);
```

## Simplified Option 2: Factory with Intercepted Parameters
```csharp
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Func<ValueTask<T>> factory,
    Action<CacheEntryOptions.Builder>? configure = null,
    params object[] keyParts)

// Usage:
var user = await cache.GetOrCreateAsync(
    () => userRepo.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1)),
    userId  // Automatically becomes part of the cache key
);
```

## Simplified Option 3: Pure Factory (Simplest)
```csharp
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Func<ValueTask<T>> factory,
    Action<CacheEntryOptions.Builder>? configure = null)

// Usage - key generated from factory method signature + captured variables:
var user = await cache.GetOrCreateAsync(
    () => userRepo.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1))
);
// Key: Hash of factory.Method + closure variables
```

## Simplified Option 4: Method String + Params
```csharp
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    string method,
    Func<ValueTask<T>> factory,
    Action<CacheEntryOptions.Builder>? configure = null,
    params object[] args)

// Usage:
var user = await cache.GetOrCreateAsync(
    "GetUser",
    () => userRepo.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1)),
    userId
);
```

## Most Aggressive: Single Factory Parameter
```csharp
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Func<ValueTask<T>> factory,
    Action<CacheEntryOptions.Builder>? configure = null)

// Implementation uses expression tree analysis to extract method + parameters
// OR uses factory.Method + closure analysis
// OR generates key from factory delegate signature

// Usage becomes identical to FluentCache:
var user = await cache.GetOrCreateAsync(
    () => userRepo.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1))
);
```

## Recommendation: Option 3 (Pure Factory)

This gives us the FluentCache experience while leveraging your sophisticated key generators:

```csharp
// Simple case
var user = await cache.GetOrCreateAsync(
    () => userRepo.GetUserAsync(userId)
);

// With configuration
var orders = await cache.GetOrCreateAsync(
    () => orderService.GetOrdersAsync(customerId, status, from, to),
    opts => opts
        .WithDuration(TimeSpan.FromMinutes(30))
        .WithTags("orders", $"customer:{customerId}")
);

// Complex objects - still works with FastHashKeyGenerator
var report = await cache.GetOrCreateAsync(
    () => reportService.GenerateAsync(complexCriteria),
    opts => opts.WithDuration(TimeSpan.FromHours(2))
);
```

## Implementation Strategy for Option 3

```csharp
public static async ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Func<ValueTask<T>> factory,
    Action<CacheEntryOptions.Builder>? configure = null,
    IServiceProvider? services = null,
    CancellationToken cancellationToken = default)
{
    // Analyze factory to extract method info and captured variables
    var keyInfo = AnalyzeFactory(factory);

    // Use your existing key generators
    var keyGenerator = services?.GetService<ICacheKeyGenerator>() ?? new FastHashKeyGenerator();
    var cacheKey = keyGenerator.GenerateKey(keyInfo.MethodName, keyInfo.Arguments, settings);

    // ... rest of caching logic
}

private static (string MethodName, object[] Arguments) AnalyzeFactory<T>(Func<ValueTask<T>> factory)
{
    // Option A: Use factory.Method.Name + analyze closure
    var method = factory.Method;
    var target = factory.Target;

    // Extract arguments from closure fields
    var arguments = ExtractClosureArguments(target);

    return (method.Name, arguments);
}
```

This approach:
1. **Eliminates manual method names** - extracted from factory
2. **Eliminates manual args arrays** - extracted from closure
3. **Leverages your key generators** - FastHashKeyGenerator handles the complexity
4. **Maintains performance** - closure analysis is cached
5. **FluentCache-like experience** - single factory parameter

Would you like me to implement Option 3?