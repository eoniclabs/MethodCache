# Enhanced Fluent API Proposal: Building on Existing Key Generators

> **Note**: Some of the features proposed in this document have been implemented, such as the `GetOrCreateAsync` overload with `methodName` and `args`, and the fluent `CacheBuilder<T>` API. The expression-based overload has not been implemented.

## Current Strengths to Preserve

MethodCache already has excellent key generation:
- **FastHashKeyGenerator**: High-performance FNV hashing with type-specific serialization
- **JsonKeyGenerator**: JSON-based key generation
- **MessagePackKeyGenerator**: Binary serialization for complex objects
- **ICacheKeyGenerator**: Flexible interface for custom generators

## Proposed Enhancement: Simplified Method-Based Caching

Instead of replacing key generators, let's make them easier to use with method-based caching:

### Current API (Manual Keys)
```csharp
var user = await cacheManager.GetOrCreateAsync(
    key: "GetUser:123",  // Manual key construction
    factory: (ctx, ct) => userRepository.GetUserAsync(userId),
    configure: opts => opts.WithDuration(TimeSpan.FromHours(1))
);
```

### Enhanced API (Method + Args + Generator)
```csharp
var user = await cacheManager.GetOrCreateAsync(
    methodName: nameof(userRepository.GetUserAsync),
    args: new object[] { userId },
    factory: (ctx, ct) => userRepository.GetUserAsync(userId),
    configure: opts => opts.WithDuration(TimeSpan.FromHours(1))
);
```

### Even Simpler: Expression-Based (Optional)
```csharp
var user = await cacheManager.GetOrCreateAsync(
    () => userRepository.GetUserAsync(userId),  // Expression extracts method + args
    opts => opts.WithDuration(TimeSpan.FromHours(1))
);
```

## Implementation Strategy

### 1. Enhanced CacheManagerExtensions
```csharp
public static class CacheManagerExtensions
{
    // Method + args version (leverages existing key generators)
    public static async ValueTask<T> GetOrCreateAsync<T>(
        this ICacheManager cacheManager,
        string methodName,
        object[] args,
        Func<CacheContext, CancellationToken, ValueTask<T>> factory,
        Action<CacheEntryOptions.Builder>? configure = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        var options = BuildOptions(configure);
        var settings = ToMethodSettings(options);

        // Use existing key generator system
        var keyGenerator = services?.GetService<ICacheKeyGenerator>() ?? new FastHashKeyGenerator();
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);

        return await cacheManager.GetOrCreateAsync(
            FluentMethodName,
            EmptyArgs,
            factory,
            settings,
            new FixedKeyGenerator(cacheKey, options.Version),
            requireIdempotent: true).ConfigureAwait(false);
    }

    // Expression version (extracts method + args, then uses above)
    public static async ValueTask<T> GetOrCreateAsync<T>(
        this ICacheManager cacheManager,
        Expression<Func<ValueTask<T>>> factoryExpression,
        Action<CacheEntryOptions.Builder>? configure = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        var (methodName, args) = ExtractMethodInfo(factoryExpression);
        var compiledFactory = factoryExpression.Compile();

        async ValueTask<T> Factory(CacheContext context, CancellationToken ct)
        {
            return await compiledFactory().ConfigureAwait(false);
        }

        return await cacheManager.GetOrCreateAsync(methodName, args, Factory, configure, services, cancellationToken);
    }
}
```

### 2. Key Generator Selection Strategy
```csharp
public static class CacheManagerExtensions
{
    // Allow explicit key generator selection
    public static async ValueTask<T> GetOrCreateAsync<T>(
        this ICacheManager cacheManager,
        Expression<Func<ValueTask<T>>> factoryExpression,
        Action<CacheEntryOptions.Builder>? configure = null,
        ICacheKeyGenerator? keyGenerator = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        var (methodName, args) = ExtractMethodInfo(factoryExpression);

        // Use provided generator or resolve from DI
        keyGenerator ??= services?.GetService<ICacheKeyGenerator>() ?? new FastHashKeyGenerator();

        // ... rest of implementation
    }
}
```

### 3. Enhanced Configuration Integration
```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMethodCache(
        this IServiceCollection services,
        Action<MethodCacheOptions> configure = null)
    {
        return services.AddMethodCache(config =>
        {
            // Set default key generator for fluent API
            config.UseKeyGenerator<FastHashKeyGenerator>();

            // Or configure per-type key generators
            config.ForType<User>()
                  .UseKeyGenerator<MessagePackKeyGenerator>(); // Binary for complex objects

            config.ForType<string>()
                  .UseKeyGenerator<JsonKeyGenerator>(); // JSON for simple types

            configure?.Invoke(config);
        });
    }
}
```

## Usage Examples: Best of Both Worlds

### 1. Simple Cases (Expression API)
```csharp
// Clean, simple syntax using existing FastHashKeyGenerator
var user = await cacheManager.GetOrCreateAsync(
    () => userRepository.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1))
);

// Generated key: FastHashKeyGenerator produces something like "UserRepository.GetUserAsync_abc123def"
```

### 2. Performance-Critical Cases (Method + Args API)
```csharp
// Direct control over method name and args, still uses key generators
var orders = await cacheManager.GetOrCreateAsync(
    methodName: "GetOrdersByCustomer",
    args: new object[] { customerId, status },
    factory: (ctx, ct) => orderService.GetOrdersByCustomerAsync(customerId, status),
    configure: opts => opts.WithDuration(TimeSpan.FromMinutes(30))
);
```

### 3. Custom Key Generator Cases
```csharp
// Use MessagePackKeyGenerator for complex objects
var report = await cacheManager.GetOrCreateAsync(
    () => analyticsService.GenerateReportAsync(complexCriteria),
    opts => opts.WithDuration(TimeSpan.FromHours(2)),
    keyGenerator: new MessagePackKeyGenerator()
);
```

### 4. Backward Compatibility (Manual Keys Still Work)
```csharp
// Existing manual key approach remains unchanged
var data = await cacheManager.GetOrCreateAsync(
    key: "custom-key-format",
    factory: (ctx, ct) => dataService.GetDataAsync(),
    configure: opts => opts.WithDuration(TimeSpan.FromMinutes(15))
);
```

## Key Generator Enhancements

### 1. Context-Aware Key Generation
```csharp
public class ContextAwareKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        var context = GetCurrentContext(); // HTTP context, tenant, etc.

        var baseKey = new FastHashKeyGenerator().GenerateKey(methodName, args, settings);

        return context != null
            ? $"{context.TenantId}:{baseKey}"
            : baseKey;
    }
}
```

### 2. Hierarchical Key Generation
```csharp
public class HierarchicalKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        // Creates hierarchical keys: "UserService:GetUser:123"
        var parts = methodName.Split('.');
        var service = parts.Length > 1 ? parts[0] : "Default";
        var method = parts.Length > 1 ? parts[1] : parts[0];

        var argPart = new FastHashKeyGenerator()
            .GenerateKey("", args, settings);

        return $"{service}:{method}:{argPart}";
    }
}
```

## Benefits of This Approach

1. **Leverages Existing Investment**: Your sophisticated key generators remain the foundation
2. **Improves Developer Experience**: Simpler API for common cases
3. **Maintains Performance**: Uses existing high-performance generators
4. **Backward Compatible**: All existing code continues to work
5. **Flexible**: Developers can choose the right level of control
6. **Type-Safe**: Expression-based approach catches refactoring issues

## Migration Path

### Phase 1: Add Enhanced Overloads
- Add method+args and expression-based overloads
- Leverage existing key generators
- Maintain full backward compatibility

### Phase 2: Enhanced Key Generator Configuration
- Add fluent configuration for key generator selection
- Type-specific key generator mapping
- Context-aware key generation

### Phase 3: Advanced Features
- Conditional key generation
- Performance optimizations
- Advanced caching patterns

This approach respects your existing investment in key generators while dramatically improving the developer experience!