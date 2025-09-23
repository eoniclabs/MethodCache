using System.Text.Json.Serialization;
using MessagePack;

namespace MethodCache.Benchmarks.Core;

/// <summary>
/// Small data model for performance testing
/// </summary>
public class SmallModel
{
    public int Id { get; set; }
    
    public string Name { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; }
    
    public static SmallModel Create(int id) => new()
    {
        Id = id,
        Name = $"Item_{id}",
        CreatedAt = DateTime.UtcNow
    };
}

/// <summary>
/// Medium data model for performance testing
/// </summary>
public class MediumModel
{
    public int Id { get; set; }
    
    public string Name { get; set; } = string.Empty;
    
    public string Description { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }
    
    public List<string> Tags { get; set; } = new();
    
    public Dictionary<string, object> Properties { get; set; } = new();
    
    public decimal Price { get; set; }
    
    public bool IsActive { get; set; }

    public static MediumModel Create(int id) => new()
    {
        Id = id,
        Name = $"Item_{id}",
        Description = $"This is a detailed description for item {id}. It contains multiple sentences to simulate real-world data sizes.",
        CreatedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(0, 365)),
        UpdatedAt = DateTime.UtcNow,
        Tags = Enumerable.Range(0, Random.Shared.Next(1, 6))
            .Select(i => $"tag_{i}_{id}")
            .ToList(),
        Properties = Enumerable.Range(0, Random.Shared.Next(1, 4))
            .ToDictionary(i => $"prop_{i}", i => (object)$"value_{i}_{id}"),
        Price = (decimal)(Random.Shared.NextDouble() * 1000),
        IsActive = Random.Shared.NextDouble() > 0.3
    };
}

/// <summary>
/// Large data model for performance testing
/// </summary>
public class LargeModel
{
    public int Id { get; set; }
    
    public string Name { get; set; } = string.Empty;
    
    public string Description { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }
    
    public List<string> Tags { get; set; } = new();
    
    public Dictionary<string, object> Properties { get; set; } = new();
    
    public List<MediumModel> RelatedItems { get; set; } = new();
    
    public byte[] BinaryData { get; set; } = Array.Empty<byte>();
    
    public List<Dictionary<string, string>> NestedData { get; set; } = new();
    
    [Key(10)]
    public decimal Price { get; set; }
    
    [Key(11)]
    public bool IsActive { get; set; }

    public static LargeModel Create(int id) => new()
    {
        Id = id,
        Name = $"Large_Item_{id}",
        Description = string.Join(" ", Enumerable.Range(0, 50)
            .Select(i => $"Description sentence {i} for item {id} with additional context and details.")),
        CreatedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(0, 365)),
        UpdatedAt = DateTime.UtcNow,
        Tags = Enumerable.Range(0, Random.Shared.Next(5, 15))
            .Select(i => $"tag_{i}_{id}")
            .ToList(),
        Properties = Enumerable.Range(0, Random.Shared.Next(5, 20))
            .ToDictionary(i => $"prop_{i}", i => (object)$"value_{i}_{id}"),
        RelatedItems = Enumerable.Range(0, Random.Shared.Next(1, 5))
            .Select(i => MediumModel.Create(id * 1000 + i))
            .ToList(),
        BinaryData = new byte[Random.Shared.Next(1024, 8192)],
        NestedData = Enumerable.Range(0, Random.Shared.Next(3, 8))
            .Select(i => Enumerable.Range(0, Random.Shared.Next(2, 6))
                .ToDictionary(j => $"nested_key_{i}_{j}", j => $"nested_value_{i}_{j}_{id}"))
            .ToList(),
        Price = (decimal)(Random.Shared.NextDouble() * 10000),
        IsActive = Random.Shared.NextDouble() > 0.2
    };
}

/// <summary>
/// User model for real-world scenarios
/// </summary>
public class User
{
    public int Id { get; set; }
    
    public string FirstName { get; set; } = string.Empty;
    
    public string LastName { get; set; } = string.Empty;
    
    public string Email { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime LastLoginAt { get; set; }
    
    public List<string> Roles { get; set; } = new();
    
    public UserPreferences Preferences { get; set; } = new();

    public static User Create(int id) => new()
    {
        Id = id,
        FirstName = $"First_{id}",
        LastName = $"Last_{id}",
        Email = $"user_{id}@benchmark.com",
        CreatedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(0, 1000)),
        LastLoginAt = DateTime.UtcNow.AddHours(-Random.Shared.Next(0, 72)),
        Roles = new[] { "User", "Viewer" }.Concat(
            Random.Shared.NextDouble() > 0.8 ? new[] { "Admin" } : Array.Empty<string>()
        ).ToList(),
        Preferences = UserPreferences.Create()
    };
}

public class UserPreferences
{
    public string Theme { get; set; } = "Light";
    
    public string Language { get; set; } = "en-US";
    
    public Dictionary<string, bool> Features { get; set; } = new();
    
    public NotificationSettings Notifications { get; set; } = new();

    public static UserPreferences Create() => new()
    {
        Theme = Random.Shared.NextDouble() > 0.5 ? "Light" : "Dark",
        Language = Random.Shared.NextDouble() > 0.7 ? "es-ES" : "en-US",
        Features = new Dictionary<string, bool>
        {
            ["feature_a"] = Random.Shared.NextDouble() > 0.5,
            ["feature_b"] = Random.Shared.NextDouble() > 0.3,
            ["feature_c"] = Random.Shared.NextDouble() > 0.7
        },
        Notifications = NotificationSettings.Create()
    };
}

public class NotificationSettings
{
    public bool EmailEnabled { get; set; }
    
    public bool PushEnabled { get; set; }
    
    public bool SmsEnabled { get; set; }

    public static NotificationSettings Create() => new()
    {
        EmailEnabled = Random.Shared.NextDouble() > 0.2,
        PushEnabled = Random.Shared.NextDouble() > 0.4,
        SmsEnabled = Random.Shared.NextDouble() > 0.8
    };
}

/// <summary>
/// Product model for e-commerce scenarios
/// </summary>
public class Product
{
    public int Id { get; set; }
    
    public string Name { get; set; } = string.Empty;
    
    public string Description { get; set; } = string.Empty;
    
    public decimal Price { get; set; }
    
    public string Category { get; set; } = string.Empty;
    
    public List<string> Tags { get; set; } = new();
    
    public ProductInventory Inventory { get; set; } = new();
    
    public List<ProductReview> Reviews { get; set; } = new();

    public static Product Create(int id) => new()
    {
        Id = id,
        Name = $"Product_{id}",
        Description = $"Detailed description for product {id} with comprehensive features and benefits.",
        Price = (decimal)(Random.Shared.NextDouble() * 500 + 10),
        Category = $"Category_{Random.Shared.Next(1, 10)}",
        Tags = Enumerable.Range(0, Random.Shared.Next(2, 8))
            .Select(i => $"tag_{i}")
            .ToList(),
        Inventory = ProductInventory.Create(),
        Reviews = Enumerable.Range(0, Random.Shared.Next(0, 5))
            .Select(i => ProductReview.Create(i))
            .ToList()
    };
}

public class ProductInventory
{
    public int Quantity { get; set; }
    
    public int ReservedQuantity { get; set; }
    
    public DateTime LastUpdated { get; set; }

    public static ProductInventory Create() => new()
    {
        Quantity = Random.Shared.Next(0, 1000),
        ReservedQuantity = Random.Shared.Next(0, 50),
        LastUpdated = DateTime.UtcNow.AddHours(-Random.Shared.Next(0, 24))
    };
}

public class ProductReview
{
    public int Id { get; set; }
    
    public int Rating { get; set; }
    
    public string Comment { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; }

    public static ProductReview Create(int id) => new()
    {
        Id = id,
        Rating = Random.Shared.Next(1, 6),
        Comment = $"Review comment {id} with detailed feedback and opinions.",
        CreatedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(0, 90))
    };
}