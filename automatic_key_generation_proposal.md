# Automatic Key Generation Proposal for MethodCache

## Overview
Add expression tree-based automatic cache key generation to simplify the developer experience while maintaining existing functionality.

## New API Overloads

### Basic Automatic Key Generation
```csharp
// New overload - automatic key generation
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Expression<Func<ValueTask<T>>> factory,
    Action<CacheEntryOptions.Builder>? configure = null,
    CancellationToken cancellationToken = default);

// Usage
var user = await cacheManager.GetOrCreateAsync(
    () => userRepository.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1))
);
```

### Synchronous Support
```csharp
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Expression<Func<T>> factory,
    Action<CacheEntryOptions.Builder>? configure = null,
    CancellationToken cancellationToken = default);

// Usage
var config = await cacheManager.GetOrCreateAsync(
    () => configService.GetConfiguration(),
    opts => opts.WithDuration(TimeSpan.FromMinutes(30))
);
```

### Parameterized Methods
```csharp
// Automatically extracts method info and parameters
var orders = await cacheManager.GetOrCreateAsync(
    () => orderService.GetOrdersByCustomerAsync(customerId, status),
    opts => opts.WithDuration(TimeSpan.FromMinutes(15))
);

// Generated key: "OrderService.GetOrdersByCustomerAsync:customerId:123:status:Active"
```

## Implementation Strategy

### 1. Expression Tree Analyzer
```csharp
public static class ExpressionKeyGenerator
{
    public static string GenerateKey(Expression expression, CacheMethodSettings? settings = null)
    {
        var analyzer = new ExpressionAnalyzer();
        var keyInfo = analyzer.Analyze(expression);

        return BuildKey(keyInfo, settings);
    }

    private static string BuildKey(KeyInfo keyInfo, CacheMethodSettings? settings)
    {
        var builder = new StringBuilder();

        // Type name
        builder.Append(keyInfo.DeclaringType.Name);
        builder.Append('.');

        // Method name
        builder.Append(keyInfo.MethodName);

        // Parameters
        foreach (var param in keyInfo.Parameters)
        {
            builder.Append(':');
            builder.Append(param.Name);
            builder.Append(':');
            builder.Append(SerializeParameter(param.Value));
        }

        // Version suffix
        if (settings?.Version.HasValue == true)
        {
            builder.Append("_v");
            builder.Append(settings.Version.Value);
        }

        return builder.ToString();
    }
}
```

### 2. Expression Analyzer
```csharp
internal class ExpressionAnalyzer
{
    public KeyInfo Analyze(Expression expression)
    {
        return expression switch
        {
            LambdaExpression lambda => AnalyzeLambda(lambda),
            _ => throw new ArgumentException("Unsupported expression type")
        };
    }

    private KeyInfo AnalyzeLambda(LambdaExpression lambda)
    {
        if (lambda.Body is MethodCallExpression methodCall)
        {
            return AnalyzeMethodCall(methodCall);
        }

        throw new ArgumentException("Expression must be a method call");
    }

    private KeyInfo AnalyzeMethodCall(MethodCallExpression methodCall)
    {
        var method = methodCall.Method;
        var parameters = new List<ParameterInfo>();

        // Extract parameter values
        for (int i = 0; i < methodCall.Arguments.Count; i++)
        {
            var arg = methodCall.Arguments[i];
            var paramName = method.GetParameters()[i].Name ?? $"arg{i}";
            var value = ExtractValue(arg);

            parameters.Add(new ParameterInfo(paramName, value));
        }

        return new KeyInfo(
            method.DeclaringType ?? typeof(object),
            method.Name,
            parameters
        );
    }

    private object? ExtractValue(Expression expression)
    {
        // Compile and execute the expression to get the actual value
        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        var lambda = Expression.Lambda(expression);
        var compiled = lambda.Compile();
        return compiled.DynamicInvoke();
    }
}
```

### 3. Integration with Existing System
```csharp
public static async ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Expression<Func<ValueTask<T>>> factoryExpression,
    Action<CacheEntryOptions.Builder>? configure = null,
    CancellationToken cancellationToken = default)
{
    // Generate key from expression
    var key = ExpressionKeyGenerator.GenerateKey(factoryExpression);

    // Compile the factory
    var compiledFactory = factoryExpression.Compile();

    // Wrap in the standard factory format
    async ValueTask<T> Factory(CacheContext context, CancellationToken ct)
    {
        return await compiledFactory().ConfigureAwait(false);
    }

    // Use existing GetOrCreateAsync overload
    return await cacheManager.GetOrCreateAsync(key, Factory, configure, cancellationToken);
}
```

## Benefits

### 1. Simplified Developer Experience
```csharp
// Before - manual key management
var user = await cacheManager.GetOrCreateAsync(
    key: $"user:{userId}",
    factory: (ctx, ct) => userRepo.GetUserAsync(userId),
    configure: opts => opts.WithDuration(TimeSpan.FromHours(1))
);

// After - automatic key generation
var user = await cacheManager.GetOrCreateAsync(
    () => userRepo.GetUserAsync(userId),
    opts => opts.WithDuration(TimeSpan.FromHours(1))
);
```

### 2. Refactoring Safety
- Method renames automatically update cache keys
- Parameter changes are reflected in keys
- No more "magic string" maintenance

### 3. Consistency
- Uniform key generation across the application
- Eliminates developer errors in key construction
- Centralized key formatting logic

### 4. Backward Compatibility
- Existing APIs remain unchanged
- New overloads are additive only
- Gradual migration path

## Performance Considerations

### Expression Compilation Caching
```csharp
private static readonly ConcurrentDictionary<string, Delegate> CompiledFactories
    = new ConcurrentDictionary<string, Delegate>();

public static async ValueTask<T> GetOrCreateAsync<T>(...)
{
    var expressionKey = factoryExpression.ToString();

    var compiledFactory = (Func<ValueTask<T>>)CompiledFactories.GetOrAdd(
        expressionKey,
        _ => factoryExpression.Compile());

    // ... rest of implementation
}
```

### Key Generation Caching
```csharp
private static readonly ConcurrentDictionary<string, string> GeneratedKeys
    = new ConcurrentDictionary<string, string>();

public static string GenerateKey(Expression expression, CacheMethodSettings? settings = null)
{
    var expressionKey = expression.ToString();

    return GeneratedKeys.GetOrAdd(expressionKey, _ =>
    {
        var analyzer = new ExpressionAnalyzer();
        var keyInfo = analyzer.Analyze(expression);
        return BuildKey(keyInfo, settings);
    });
}
```

## Advanced Features

### Custom Key Generation Strategies
```csharp
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Expression<Func<ValueTask<T>>> factory,
    IExpressionKeyGenerator keyGenerator,
    Action<CacheEntryOptions.Builder>? configure = null,
    CancellationToken cancellationToken = default);

// Custom key generators for specific scenarios
public class HashBasedKeyGenerator : IExpressionKeyGenerator
{
    public string GenerateKey(Expression expression, CacheMethodSettings? settings)
    {
        var defaultKey = ExpressionKeyGenerator.GenerateKey(expression, settings);
        return ComputeHash(defaultKey); // Shorter keys for very long parameter lists
    }
}
```

### Conditional Key Generation
```csharp
// Only generate keys for specific types/methods
public static ValueTask<T> GetOrCreateAsync<T>(
    this ICacheManager cacheManager,
    Expression<Func<ValueTask<T>>> factory,
    Func<KeyInfo, bool> shouldCache,
    Action<CacheEntryOptions.Builder>? configure = null,
    CancellationToken cancellationToken = default);

// Usage
var result = await cacheManager.GetOrCreateAsync(
    () => expensiveService.CalculateAsync(data),
    keyInfo => keyInfo.Parameters.Any(p => p.Name == "largeDataSet"),
    opts => opts.WithDuration(TimeSpan.FromHours(2))
);
```

## Migration Path

### Phase 1: Add New Overloads
- Implement expression-based overloads
- Maintain full backward compatibility
- Add comprehensive tests

### Phase 2: Documentation & Examples
- Update documentation with new patterns
- Provide migration guides
- Show performance comparisons

### Phase 3: Optimization
- Add expression caching
- Optimize key generation
- Performance benchmarking

### Phase 4: Enhanced Features
- Custom key generators
- Advanced expression analysis
- Integration with existing key generators

## Conclusion

This proposal would significantly improve the developer experience of MethodCache by:
1. Eliminating manual key management for common scenarios
2. Reducing boilerplate code
3. Making caching more refactoring-safe
4. Maintaining high performance through caching
5. Preserving backward compatibility

The implementation leverages C#'s expression tree capabilities to provide automatic key generation while maintaining all the advanced features and performance characteristics that make MethodCache powerful.