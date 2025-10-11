using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Runtime;

namespace MethodCache.SourceGenerator.IntegrationTests.Infrastructure
{
    /// <summary>
    /// Universal test models and interfaces that can be used across all integration tests
    /// </summary>
    public static class UniversalTestModels
    {
        public static string GetCompleteModelDefinitions() => @"
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

    public class LargeObject
    {
        public int Id { get; set; }
        public string Data { get; set; } = new string('A', 10000); // 10KB of data
        public List<string> Items { get; set; } = new();
    }

    // Cache condition interface and implementations
    public interface ICacheCondition
    {
        bool ShouldCache();
    }

    public class AlwaysCacheCondition : ICacheCondition
    {
        public bool ShouldCache() => true;
    }

    public class NeverCacheCondition : ICacheCondition
    {
        public bool ShouldCache() => false;
    }

    // Custom cache key generator
    public class CustomKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
        {
            return $""CUSTOM_{methodName}_{string.Join(""_"", args)}"";
        }
    }

    // Test exceptions
    public class TestException : Exception
    {
        public TestException(string message) : base(message) { }
    }";

        public static string GetRequiredUsings() => @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Runtime;";
    }
}