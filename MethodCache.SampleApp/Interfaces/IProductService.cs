using MethodCache.Core;
using MethodCache.SampleApp.Models;
using System.Threading.Tasks;
using MethodCache.Core.Configuration.Surfaces.Attributes;

namespace MethodCache.SampleApp.Interfaces
{
    /// <summary>
    /// Product service demonstrating advanced caching features
    /// </summary>
    public interface IProductService
    {
        // Basic product retrieval
        [Cache("Products")]
        Task<Product> GetProductAsync(int productId);

        // Product search with complex parameters
        [Cache("ProductSearch")]
        Task<List<Product>> SearchProductsAsync(ProductSearchCriteria criteria);

        // Expensive calculation that should be cached
        [Cache("ProductCalculations")]
        Task<decimal> CalculateProductPriceAsync(int productId, string customerTier, DateTime date);

        // Product categories with longer cache duration (configured via fluent API)
        [Cache("ProductCategories")]
        Task<List<ProductCategory>> GetProductCategoriesAsync();

        // Product inventory that changes frequently
        [Cache("ProductInventory")]
        Task<ProductInventory> GetProductInventoryAsync(int productId);

        // Cache invalidation for product updates
        [CacheInvalidate(Tags = new[] { "Products", "ProductSearch", "ProductCalculations" })]
        Task UpdateProductAsync(int productId, Product product);

        // Cache invalidation for inventory updates
        [CacheInvalidate(Tags = new[] { "ProductInventory" })]
        Task UpdateInventoryAsync(int productId, int quantity);

        // Cache invalidation for category updates
        [CacheInvalidate(Tags = new[] { "ProductCategories", "ProductSearch" })]
        Task UpdateCategoryAsync(int categoryId, ProductCategory category);

        // Bulk operations
        [Cache("ProductBulk")]
        Task<List<Product>> GetProductsByIdsAsync(int[] productIds);

        // Generic methods (temporarily commented out due to source generator limitations)
        // [Cache]
        // Task<T> GetProductPropertyAsync<T>(int productId, string propertyName);

        // Methods with no parameters
        [Cache]
        Task<ProductStatistics> GetGlobalProductStatisticsAsync();

        // Featured products with caching
        [Cache("FeaturedProducts")]
        Task<List<Product>> GetFeaturedProductsAsync(int count);
        
        // Generic product details (currently commented out due to source generator limitations)
        // [Cache("ProductDetails")]
        // Task<T> GetProductDetailsAsync<T>(int productId) where T : class;
        
        // Related products with caching
        [Cache("RelatedProducts")]
        Task<List<Product>> GetRelatedProductsAsync(int productId, int count);

        // Method that shouldn't be cached (frequent updates)
        Task TrackProductViewAsync(int productId, int userId);
    }
}