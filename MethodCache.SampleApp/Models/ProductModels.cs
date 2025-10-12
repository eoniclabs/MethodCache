using MethodCache.Core;
using MethodCache.Core.Runtime;

namespace MethodCache.SampleApp.Models
{
    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int CategoryId { get; set; }
        public string SKU { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int StockQuantity { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    public class ProductCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int? ParentCategoryId { get; set; }
        public bool IsActive { get; set; }
    }

    public class ProductInventory
    {
        public int ProductId { get; set; }
        public int QuantityOnHand { get; set; }
        public int QuantityReserved { get; set; }
        public int QuantityAvailable => QuantityOnHand - QuantityReserved;
        public DateTime LastUpdated { get; set; }
    }

    public class ProductSearchCriteria : ICacheKeyProvider
    {
        public string? Query { get; set; }
        public int? CategoryId { get; set; }
        public decimal? MinPrice { get; set; }
        public decimal? MaxPrice { get; set; }
        public bool? InStock { get; set; }
        public string? SortBy { get; set; }
        public bool SortDescending { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; } = 20;

        public string CacheKeyPart =>
            $"query:{Query ?? "null"}_cat:{CategoryId?.ToString() ?? "null"}_minPrice:{MinPrice?.ToString() ?? "null"}_maxPrice:{MaxPrice?.ToString() ?? "null"}_inStock:{InStock?.ToString() ?? "null"}_sort:{SortBy ?? "null"}_desc:{SortDescending}_skip:{Skip}_take:{Take}";
    }

    public class ProductStatistics
    {
        public int TotalProducts { get; set; }
        public int ActiveProducts { get; set; }
        public int TotalCategories { get; set; }
        public decimal AveragePrice { get; set; }
        public DateTime GeneratedAt { get; set; }
    }
}