# Smart Key Generation Enhancement

## Current vs Smart Key Generation

### Current Behavior
```csharp
await cache.GetOrCreateAsync(() => userService.GetUserAsync(userId));
// Key: "GetUserAsync_a1b2c3d4e5f6g7h8"

await cache.GetOrCreateAsync(() => orderRepo.GetOrdersByCustomerAsync(customerId, status));
// Key: "GetOrdersByCustomerAsync_a1b2c3d4e5f6g7h8"
```

### Smart Key Generation
```csharp
await cache.GetOrCreateAsync(() => userService.GetUserAsync(userId))
    .WithSmartKeying();
// Key: "UserService:GetUser:123" (service prefix + simplified method + key args)

await cache.GetOrCreateAsync(() => orderRepo.GetOrdersByCustomerAsync(customerId, status))
    .WithSmartKeying();
// Key: "OrderRepo:GetOrdersByCustomer:cust:123:status:Active"
```

## Smart Key Generation Features

### 1. **Service/Class Name Detection**
```csharp
// Current
() => userService.GetUserAsync(userId)
// Key: "GetUserAsync_hash"

// Smart
() => userService.GetUserAsync(userId)
// Key: "UserService:GetUser:123"
//      ^^^^^^^^^^^  ^^^^^^^  ^^^
//      Service      Method   Args
```

### 2. **Method Name Simplification**
```csharp
// Removes common suffixes and prefixes
"GetUserAsync" → "GetUser"
"FetchOrdersAsync" → "FetchOrders"
"RetrieveDataAsync" → "RetrieveData"
"SaveUserDataAsync" → "SaveUserData"
```

### 3. **Argument Type Detection**
```csharp
// Current: All args treated equally
userId: 123, includeProfile: true
// Key: "GetUser_a1b2c3d4e5f6g7h8"

// Smart: Recognizes argument types
userId: 123, includeProfile: true
// Key: "UserService:GetUser:user:123:profile:true"
//                           ^^^^      ^^^^^^^
//                           ID type   Boolean flag
```

### 4. **Pattern Recognition**
```csharp
// Paged Results Pattern
() => repo.GetUsersAsync(page: 1, pageSize: 20)
// Smart Key: "UserRepo:GetUsers:page:1:size:20"

// Search Pattern
() => service.SearchAsync(query, filters)
// Smart Key: "SearchService:Search:query:hash:filters:hash"

// ID-based Pattern
() => repo.GetByIdAsync(123)
// Smart Key: "UserRepo:GetById:123"
```

### 5. **Hierarchical Keys**
```csharp
// Smart keying creates logical hierarchies
await cache.GetOrCreateAsync(() => userService.GetUserAsync(userId))
    .WithSmartKeying();
// Key: "UserService:GetUser:123"

await cache.GetOrCreateAsync(() => userService.GetUserProfileAsync(userId))
    .WithSmartKeying();
// Key: "UserService:GetUserProfile:123"

// This enables:
// - Better cache organization
// - Easier bulk invalidation
// - Pattern-based cache management
```

## Implementation Approach

### Enhanced Factory Analysis
```csharp
private static SmartKeyInfo AnalyzeFactoryForSmartKey(Delegate factory)
{
    var methodCall = ExtractMethodCallFromFactory(factory);

    return new SmartKeyInfo
    {
        ServiceName = ExtractServiceName(methodCall.Target),
        MethodName = SimplifyMethodName(methodCall.Method.Name),
        Arguments = ClassifyArguments(methodCall.Arguments),
        Pattern = DetectPattern(methodCall)
    };
}

private static string ExtractServiceName(object target)
{
    var typeName = target.GetType().Name;

    // Remove common suffixes
    return typeName
        .Replace("Service", "")
        .Replace("Repository", "Repo")
        .Replace("Manager", "Mgr")
        .Replace("Controller", "")
        .Replace("Handler", "");
}

private static string SimplifyMethodName(string methodName)
{
    return methodName
        .Replace("Async", "")
        .Replace("Get", "Get")
        .Replace("Fetch", "Get")  // Normalize to consistent verbs
        .Replace("Retrieve", "Get");
}

private static ArgumentInfo[] ClassifyArguments(object[] args)
{
    return args.Select((arg, index) => new ArgumentInfo
    {
        Value = arg,
        Type = ClassifyArgumentType(arg),
        Name = InferArgumentName(arg, index)
    }).ToArray();
}

private static ArgumentType ClassifyArgumentType(object arg)
{
    return arg switch
    {
        int or long => ArgumentType.Id,
        string s when s.Length > 50 => ArgumentType.LargeText,
        string => ArgumentType.SimpleText,
        bool => ArgumentType.Flag,
        DateTime => ArgumentType.Date,
        Enum => ArgumentType.Enum,
        _ when arg.GetType().Name.Contains("Criteria") => ArgumentType.SearchCriteria,
        _ when arg.GetType().Name.Contains("Request") => ArgumentType.Request,
        _ => ArgumentType.Complex
    };
}
```

### Smart Key Generation Options
```csharp
public class SmartKeyingOptions
{
    public bool UseServicePrefix { get; set; } = true;
    public bool SimplifyMethodNames { get; set; } = true;
    public bool ClassifyArguments { get; set; } = true;
    public bool UseHierarchicalKeys { get; set; } = true;
    public int MaxKeyLength { get; set; } = 200;
    public KeyGenerationPattern Pattern { get; set; } = KeyGenerationPattern.Auto;
}

public enum KeyGenerationPattern
{
    Auto,           // Detect pattern automatically
    IdBased,        // Optimize for ID-based lookups
    SearchBased,    // Optimize for search operations
    PagedBased,     // Optimize for paged results
    Hierarchical    // Use hierarchical naming
}
```

## Usage Examples

### Basic Smart Keying
```csharp
// Enable smart keying
await cache.GetOrCreateAsync(() => userService.GetUserAsync(userId))
    .WithSmartKeying()
    .ExecuteAsync();
// Key: "UserService:GetUser:123"
```

### Customized Smart Keying
```csharp
await cache.GetOrCreateAsync(() => searchService.SearchUsersAsync(query, filters))
    .WithSmartKeying(options => options
        .UsePattern(KeyGenerationPattern.SearchBased)
        .MaxKeyLength(150)
        .UseServicePrefix(false))
    .ExecuteAsync();
// Key: "SearchUsers:query:hash:filters:hash"
```

### Pattern-Specific Optimizations
```csharp
// ID-based pattern
await cache.GetOrCreateAsync(() => repo.GetByIdAsync(123))
    .WithSmartKeying(KeyGenerationPattern.IdBased)
    .ExecuteAsync();
// Key: "Repo:GetById:123"

// Paged pattern
await cache.GetOrCreateAsync(() => service.GetPagedAsync(page, size))
    .WithSmartKeying(KeyGenerationPattern.PagedBased)
    .ExecuteAsync();
// Key: "Service:GetPaged:p1:s20"

// Search pattern
await cache.GetOrCreateAsync(() => service.SearchAsync(criteria))
    .WithSmartKeying(KeyGenerationPattern.SearchBased)
    .ExecuteAsync();
// Key: "Service:Search:criteria_hash"
```

## Benefits

### 1. **Human-Readable Keys**
- Easier debugging and monitoring
- Better cache analytics
- Simpler cache management

### 2. **Better Cache Organization**
```csharp
// Hierarchical organization enables:
cache.InvalidateByPattern("UserService:*");     // All user service calls
cache.InvalidateByPattern("*:GetUser:*");       // All GetUser calls
cache.InvalidateByPattern("UserService:GetUser:123"); // Specific user
```

### 3. **Optimized for Common Patterns**
- ID-based lookups get shorter, more efficient keys
- Search operations get optimized hash representation
- Paged results get compact page/size encoding

### 4. **Backward Compatibility**
- Smart keying is opt-in
- Falls back to current behavior when pattern detection fails
- Can be disabled per call

## Implementation Phases

### Phase 1: Basic Smart Analysis
- Service name extraction
- Method name simplification
- Argument classification

### Phase 2: Pattern Detection
- ID-based pattern detection
- Search pattern detection
- Paged results pattern detection

### Phase 3: Advanced Features
- Custom pattern definitions
- Machine learning for pattern detection
- Performance optimization based on usage patterns

## Comparison

| Feature | Current | Smart |
|---------|---------|-------|
| Keys | `GetUserAsync_hash` | `UserService:GetUser:123` |
| Readability | Low | High |
| Debuggability | Hard | Easy |
| Organization | Flat | Hierarchical |
| Pattern Support | None | Built-in |
| Invalidation | Key-specific | Pattern-based |
| Configuration | Key generator only | Pattern + generator |

Smart Key Generation transforms cache keys from opaque hashes into meaningful, organized identifiers while maintaining all the performance benefits of the underlying key generators!