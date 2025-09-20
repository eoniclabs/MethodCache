namespace MethodCache.SourceGenerator.IntegrationTests.Models;

/// <summary>
/// Test models for integration testing
/// </summary>

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    
    public override bool Equals(object? obj)
    {
        return obj is User user && Id == user.Id && Name == user.Name;
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Name);
    }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Category { get; set; } = string.Empty;
    public bool InStock { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public List<OrderItem> Items { get; set; } = new();
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
    public OrderStatus Status { get; set; }
}

public class OrderItem
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public enum OrderStatus
{
    Pending,
    Processing,
    Shipped,
    Delivered,
    Cancelled
}

public class CacheMetrics
{
    public int HitCount { get; set; }
    public int MissCount { get; set; }
    public int ErrorCount { get; set; }
    public Dictionary<string, int> TagInvalidations { get; set; } = new();
    
    public double HitRatio => HitCount + MissCount > 0 ? (double)HitCount / (HitCount + MissCount) : 0;
    
    public void Reset()
    {
        HitCount = 0;
        MissCount = 0;
        ErrorCount = 0;
        TagInvalidations.Clear();
    }
}