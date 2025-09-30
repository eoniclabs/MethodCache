# Adding Caching to an Existing Service

This guide walks you through adding MethodCache to an existing service with minimal code changes.

## Prerequisites

```bash
dotnet add package MethodCache.Core
dotnet add package MethodCache.SourceGenerator
```

## Quick Start: 3-Step Process

### Step 1: Install and Register

```csharp
// In Program.cs or Startup.cs
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10))
          .DefaultKeyGenerator<FastHashKeyGenerator>();
});
```

### Step 2: Extract Interface (if needed)

MethodCache works best with interfaces. If your service doesn't have one, extract it:

```csharp
// Before
public class ProductService
{
    public Task<Product> GetProductAsync(int productId) { ... }
}

// After
public interface IProductService
{
    Task<Product> GetProductAsync(int productId);
}

public class ProductService : IProductService
{
    public Task<Product> GetProductAsync(int productId) { ... }
}
```

### Step 3: Add Cache Attributes

```csharp
public interface IProductService
{
    [Cache(Duration = "00:30:00", Tags = new[] { "products", "product:{productId}" })]
    Task<Product> GetProductAsync(int productId);
}
```

That's it! MethodCache will automatically cache the results.

## Real-World Example

Let's add caching to a typical service that fetches data from a database and external API.

### Original Service (No Caching)

```csharp
public class OrderService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OrderService> _logger;

    public OrderService(AppDbContext db, IHttpClientFactory httpFactory, ILogger<OrderService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<Order> GetOrderAsync(int orderId)
    {
        _logger.LogInformation("Fetching order {OrderId}", orderId);

        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
            throw new NotFoundException($"Order {orderId} not found");

        return order;
    }

    public async Task<OrderStatus> GetOrderStatusAsync(int orderId)
    {
        var http = _httpFactory.CreateClient("shipping");
        var response = await http.GetFromJsonAsync<OrderStatus>($"/api/status/{orderId}");
        return response;
    }

    public async Task UpdateOrderAsync(int orderId, OrderUpdateDto dto)
    {
        var order = await _db.Orders.FindAsync(orderId);
        if (order == null)
            throw new NotFoundException($"Order {orderId} not found");

        order.Status = dto.Status;
        order.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated order {OrderId}", orderId);
    }

    public async Task<List<Order>> GetCustomerOrdersAsync(int customerId)
    {
        return await _db.Orders
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }
}
```

### With MethodCache (Attribute-Based)

```csharp
// 1. Extract interface
public interface IOrderService
{
    Task<Order> GetOrderAsync(int orderId);
    Task<OrderStatus> GetOrderStatusAsync(int orderId);
    Task UpdateOrderAsync(int orderId, OrderUpdateDto dto);
    Task<List<Order>> GetCustomerOrdersAsync(int customerId);
}

// 2. Add cache attributes
public interface IOrderService
{
    // Cache for 30 minutes - database data
    [Cache(Duration = "00:30:00",
           Tags = new[] { "orders", "order:{orderId}" },
           RequireIdempotent = true)]
    Task<Order> GetOrderAsync(int orderId);

    // Cache for 2 minutes - external API, changes frequently
    [Cache(Duration = "00:02:00",
           Tags = new[] { "order-status", "order:{orderId}" },
           RequireIdempotent = true)]
    Task<OrderStatus> GetOrderStatusAsync(int orderId);

    // Invalidate related caches when updating
    [CacheInvalidate(Tags = new[] { "orders", "order:{orderId}", "order-status", "customer-orders" })]
    Task UpdateOrderAsync(int orderId, OrderUpdateDto dto);

    // Cache for 10 minutes - customer order list
    [Cache(Duration = "00:10:00",
           Tags = new[] { "customer-orders", "customer:{customerId}" },
           RequireIdempotent = true)]
    Task<List<Order>> GetCustomerOrdersAsync(int customerId);
}

// 3. Implementation stays the same!
public class OrderService : IOrderService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OrderService> _logger;

    public OrderService(AppDbContext db, IHttpClientFactory httpFactory, ILogger<OrderService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<Order> GetOrderAsync(int orderId)
    {
        _logger.LogInformation("Fetching order {OrderId}", orderId);

        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
            throw new NotFoundException($"Order {orderId} not found");

        return order;
    }

    public async Task<OrderStatus> GetOrderStatusAsync(int orderId)
    {
        var http = _httpFactory.CreateClient("shipping");
        var response = await http.GetFromJsonAsync<OrderStatus>($"/api/status/{orderId}");
        return response;
    }

    public async Task UpdateOrderAsync(int orderId, OrderUpdateDto dto)
    {
        var order = await _db.Orders.FindAsync(orderId);
        if (order == null)
            throw new NotFoundException($"Order {orderId} not found");

        order.Status = dto.Status;
        order.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated order {OrderId}", orderId);
    }

    public async Task<List<Order>> GetCustomerOrdersAsync(int customerId)
    {
        return await _db.Orders
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }
}
```

### Register the Service

```csharp
builder.Services.AddScoped<IOrderService, OrderService>();
```

## Alternative: Fluent API (No Interface Changes)

If you can't or don't want to modify interfaces, use the fluent API:

```csharp
public class OrderService
{
    private readonly ICacheManager _cache;
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;

    public async Task<Order> GetOrderAsync(int orderId)
    {
        return await _cache.Cache(() => FetchOrderFromDbAsync(orderId))
            .WithDuration(TimeSpan.FromMinutes(30))
            .WithTags("orders", $"order:{orderId}")
            .ExecuteAsync();
    }

    private async Task<Order> FetchOrderFromDbAsync(int orderId)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
            throw new NotFoundException($"Order {orderId} not found");

        return order;
    }

    public async Task UpdateOrderAsync(int orderId, OrderUpdateDto dto)
    {
        var order = await _db.Orders.FindAsync(orderId);
        if (order == null)
            throw new NotFoundException($"Order {orderId} not found");

        order.Status = dto.Status;
        order.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // Invalidate related caches
        await _cache.InvalidateByTagsAsync("orders", $"order:{orderId}", "order-status");
    }
}
```

## Choosing Cache Duration

| Data Type | Recommended Duration | Example |
|-----------|---------------------|---------|
| **Static/Reference Data** | 1-24 hours | Categories, countries, product types |
| **Slowly Changing** | 30-60 minutes | Product details, user profiles |
| **Moderate Changes** | 5-30 minutes | Order lists, search results |
| **Frequently Changing** | 1-5 minutes | Stock quantities, order status |
| **Real-time Data** | 30 seconds - 2 minutes | Live scores, prices, tracking |

### Example Duration Settings

```csharp
// Static reference data - cache for hours
[Cache(Duration = "01:00:00")]  // 1 hour
Task<List<Category>> GetCategoriesAsync();

// User profile - moderate duration
[Cache(Duration = "00:30:00")]  // 30 minutes
Task<UserProfile> GetUserProfileAsync(int userId);

// Product list - shorter duration
[Cache(Duration = "00:10:00")]  // 10 minutes
Task<List<Product>> GetProductsAsync(ProductFilter filter);

// External API status - very short
[Cache(Duration = "00:02:00")]  // 2 minutes
Task<ApiStatus> GetApiStatusAsync();

// Real-time data - minimal cache
[Cache(Duration = "00:00:30")]  // 30 seconds
Task<LivePrice> GetStockPriceAsync(string symbol);
```

## Tag Strategy for Invalidation

Good tag strategy is crucial for cache coherency:

### Pattern 1: Entity Type + Entity ID

```csharp
[Cache(Tags = new[] { "products", "product:{productId}" })]
Task<Product> GetProductAsync(int productId);

[CacheInvalidate(Tags = new[] { "products", "product:{productId}" })]
Task UpdateProductAsync(int productId, ProductUpdateDto dto);
```

### Pattern 2: Feature Area

```csharp
[Cache(Tags = new[] { "catalog", "products" })]
Task<List<Product>> GetAllProductsAsync();

[Cache(Tags = new[] { "catalog", "categories" })]
Task<List<Category>> GetCategoriesAsync();

// Invalidate entire catalog
[CacheInvalidate(Tags = new[] { "catalog" })]
Task RefreshCatalogAsync();
```

### Pattern 3: User/Tenant Scoped

```csharp
[Cache(Tags = new[] { "user-data", "user:{userId}" })]
Task<UserSettings> GetUserSettingsAsync(int userId);

[Cache(Tags = new[] { "tenant-data", "tenant:{tenantId}" })]
Task<TenantConfig> GetTenantConfigAsync(string tenantId);

// Invalidate all caches for a user
await _cache.InvalidateByTagsAsync($"user:{userId}");
```

## Common Patterns

### Pattern 1: List + Detail Caching

```csharp
// Cache the list
[Cache(Duration = "00:10:00", Tags = new[] { "products" })]
Task<List<ProductSummary>> GetProductSummariesAsync();

// Cache individual items with longer duration
[Cache(Duration = "00:30:00", Tags = new[] { "products", "product:{productId}" })]
Task<ProductDetail> GetProductDetailAsync(int productId);

// Update invalidates both
[CacheInvalidate(Tags = new[] { "products", "product:{productId}" })]
Task UpdateProductAsync(int productId, ProductUpdateDto dto);
```

### Pattern 2: Dependent Data

```csharp
// Parent entity
[Cache(Tags = new[] { "orders", "order:{orderId}" })]
Task<Order> GetOrderAsync(int orderId);

// Dependent entities
[Cache(Tags = new[] { "order-items", "order:{orderId}" })]
Task<List<OrderItem>> GetOrderItemsAsync(int orderId);

// Update invalidates all dependent caches
[CacheInvalidate(Tags = new[] { "orders", "order:{orderId}", "order-items" })]
Task UpdateOrderAsync(int orderId, OrderUpdateDto dto);
```

### Pattern 3: Aggregate Queries

```csharp
// Specific query
[Cache(Tags = new[] { "order-summary", "customer:{customerId}" })]
Task<OrderSummary> GetOrderSummaryAsync(int customerId, DateRange range);

// Broader query
[Cache(Tags = new[] { "order-reports" })]
Task<SalesReport> GetSalesReportAsync(DateRange range);

// Any order change invalidates summaries
[CacheInvalidate(Tags = new[] { "order-summary", "order-reports" })]
Task CreateOrderAsync(CreateOrderDto dto);
```

## Testing Cache Behavior

### Verify Cache Hits

```csharp
[Fact]
public async Task GetProduct_SecondCall_ShouldHitCache()
{
    // Arrange
    var service = GetService<IProductService>();

    // Act - First call (miss)
    var product1 = await service.GetProductAsync(123);
    var product2 = await service.GetProductAsync(123);

    // Assert - Should be same instance from cache
    Assert.Same(product1, product2);
}
```

### Verify Invalidation

```csharp
[Fact]
public async Task UpdateProduct_ShouldInvalidateCache()
{
    // Arrange
    var service = GetService<IProductService>();

    // Act
    var product1 = await service.GetProductAsync(123);
    await service.UpdateProductAsync(123, new ProductUpdateDto());
    var product2 = await service.GetProductAsync(123);

    // Assert - Should be different instances
    Assert.NotSame(product1, product2);
}
```

## Performance Monitoring

Add metrics to track cache effectiveness:

```csharp
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10))
          .DefaultKeyGenerator<FastHashKeyGenerator>();
});

// Custom metrics provider
builder.Services.AddSingleton<ICacheMetricsProvider, CustomMetricsProvider>();
```

## Troubleshooting

### Cache Not Working

**Symptom:** Methods always execute, never hit cache

**Checklist:**
- [ ] Is `AddMethodCache()` called in DI?
- [ ] Is interface registered in DI?
- [ ] Is `Duration` set on `[Cache]` attribute?
- [ ] Is method virtual or interface member?
- [ ] Is source generator running? (check generated files)

**Solution:**
```csharp
// Ensure proper registration
builder.Services.AddMethodCache(config =>
    config.DefaultDuration(TimeSpan.FromMinutes(10)));
builder.Services.AddScoped<IProductService, ProductService>();
```

### Cache Not Invalidating

**Symptom:** Stale data returned after updates

**Solution:** Ensure tags match between `[Cache]` and `[CacheInvalidate]`:

```csharp
// ✅ Good - tags match
[Cache(Tags = new[] { "products", "product:{productId}" })]
Task<Product> GetProductAsync(int productId);

[CacheInvalidate(Tags = new[] { "products", "product:{productId}" })]
Task UpdateProductAsync(int productId, ProductUpdateDto dto);

// ❌ Bad - tags don't match
[Cache(Tags = new[] { "product" })]
Task<Product> GetProductAsync(int productId);

[CacheInvalidate(Tags = new[] { "products" })]  // Won't invalidate!
Task UpdateProductAsync(int productId, ProductUpdateDto dto);
```

## Next Steps

1. **Add Analyzers**: `dotnet add package MethodCache.Analyzers` for compile-time validation
2. **Enable Distributed Cache**: Add `MethodCache.Providers.Redis` for multi-instance apps
3. **Monitor Performance**: Implement `ICacheMetricsProvider` for observability
4. **Optimize**: Switch to `FastHashKeyGenerator` for best performance

## Resources

- [Configuration Guide](../user-guide/CONFIGURATION_GUIDE.md)
- [Migration from IMemoryCache](MIGRATION_FROM_IMEMORYCACHE.md)
- [Fluent API Reference](../user-guide/fluent-api.md)