using MethodCache.SampleApp.Interfaces;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp.Services
{
    public class ProductService : IProductService
    {
        private readonly List<Product> _products;
        private readonly Random _random = new();

        public ProductService()
        {
            _products = GenerateSampleProducts();
        }

        public async Task<Product?> GetProductByIdAsync(int productId)
        {
            // Simulate database delay
            await Task.Delay(_random.Next(50, 150));
            
            Console.WriteLine($"[ProductService] Fetching product {productId} from database...");
            return _products.FirstOrDefault(p => p.Id == productId);
        }

        public async Task<List<Product>> GetProductsByCategoryAsync(int categoryId)
        {
            // Simulate category lookup
            await Task.Delay(_random.Next(100, 300));
            
            Console.WriteLine($"[ProductService] Loading products for category {categoryId}...");
            return _products.Where(p => p.CategoryId == categoryId).ToList();
        }

        public async Task<List<Product>> SearchProductsAsync(ProductSearchCriteria criteria)
        {
            // Simulate complex search with filters
            await Task.Delay(_random.Next(200, 500));
            
            Console.WriteLine($"[ProductService] Performing product search: {criteria.CacheKeyPart}...");
            
            var query = _products.AsQueryable();

            if (!string.IsNullOrEmpty(criteria.Query))
                query = query.Where(p => p.Name.Contains(criteria.Query, StringComparison.OrdinalIgnoreCase) ||
                                       p.Description.Contains(criteria.Query, StringComparison.OrdinalIgnoreCase));

            if (criteria.CategoryId.HasValue)
                query = query.Where(p => p.CategoryId == criteria.CategoryId.Value);

            if (criteria.MinPrice.HasValue)
                query = query.Where(p => p.Price >= criteria.MinPrice.Value);

            if (criteria.MaxPrice.HasValue)
                query = query.Where(p => p.Price <= criteria.MaxPrice.Value);

            if (criteria.InStock.HasValue)
                query = query.Where(p => criteria.InStock.Value ? p.StockQuantity > 0 : p.StockQuantity == 0);

            if (!string.IsNullOrEmpty(criteria.SortBy))
            {
                query = criteria.SortBy.ToLower() switch
                {
                    "name" => criteria.SortDescending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
                    "price" => criteria.SortDescending ? query.OrderByDescending(p => p.Price) : query.OrderBy(p => p.Price),
                    "stock" => criteria.SortDescending ? query.OrderByDescending(p => p.StockQuantity) : query.OrderBy(p => p.StockQuantity),
                    _ => query.OrderBy(p => p.Id)
                };
            }

            var results = query.Skip(criteria.Skip).Take(criteria.Take).ToList();
            Console.WriteLine($"[ProductService] Search returned {results.Count} products");
            return results;
        }

        public async Task<List<Product>> GetFeaturedProductsAsync(int count)
        {
            // Simulate expensive featured products calculation
            await Task.Delay(_random.Next(300, 600));
            
            Console.WriteLine($"[ProductService] Calculating top {count} featured products...");
            
            // Simulate complex algorithm for featured products
            var featured = _products
                .Where(p => p.StockQuantity > 0)
                .OrderByDescending(p => p.Price * (decimal)_random.NextDouble()) // Simulate popularity score
                .Take(count)
                .ToList();
                
            return featured;
        }

        public async Task<T> GetProductDetailsAsync<T>(int productId) where T : class
        {
            // Simulate polymorphic product details loading
            await Task.Delay(_random.Next(100, 250));
            
            Console.WriteLine($"[ProductService] Loading product details as {typeof(T).Name} for product {productId}...");
            
            var product = _products.FirstOrDefault(p => p.Id == productId);
            if (product == null)
                return default(T)!;

            // For demo purposes, return the product regardless of T
            // In real scenarios, you'd have different detail types
            return (T)(object)product;
        }

        public async Task<List<Product>> GetRelatedProductsAsync(int productId, int count)
        {
            // Simulate related products calculation
            await Task.Delay(_random.Next(200, 400));
            
            Console.WriteLine($"[ProductService] Finding {count} related products for product {productId}...");
            
            var product = _products.FirstOrDefault(p => p.Id == productId);
            if (product == null)
                return new List<Product>();

            // Simple related products logic - same category, different product
            var related = _products
                .Where(p => p.CategoryId == product.CategoryId && p.Id != productId)
                .OrderBy(_ => _random.Next())
                .Take(count)
                .ToList();
                
            return related;
        }

        public async Task<Product> CreateProductAsync(string name, decimal price, int categoryId, string description)
        {
            // Simulate product creation
            await Task.Delay(_random.Next(200, 400));
            
            Console.WriteLine($"[ProductService] Creating new product: {name} (${price})");
            
            var newProduct = new Product
            {
                Id = _products.Max(p => p.Id) + 1,
                Name = name,
                Price = price,
                CategoryId = categoryId,
                Description = description,
                StockQuantity = _random.Next(0, 100),
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };
            
            _products.Add(newProduct);
            return newProduct;
        }

        public async Task<Product> UpdateProductAsync(int productId, string name, decimal price, string description)
        {
            // Simulate product update
            await Task.Delay(_random.Next(150, 300));
            
            Console.WriteLine($"[ProductService] Updating product {productId}: {name} (${price})");
            
            var product = _products.FirstOrDefault(p => p.Id == productId);
            if (product == null)
                throw new ArgumentException($"Product {productId} not found");

            product.Name = name;
            product.Price = price;
            product.Description = description;
            product.UpdatedAt = DateTime.UtcNow;
            
            return product;
        }

        public async Task DeleteProductAsync(int productId)
        {
            // Simulate product deletion
            await Task.Delay(_random.Next(100, 250));
            
            Console.WriteLine($"[ProductService] Deleting product {productId}");
            
            var product = _products.FirstOrDefault(p => p.Id == productId);
            if (product != null)
            {
                _products.Remove(product);
            }
        }

        public async Task UpdateStockAsync(int productId, int newQuantity)
        {
            // Simulate stock update
            await Task.Delay(_random.Next(50, 150));
            
            Console.WriteLine($"[ProductService] Updating stock for product {productId} to {newQuantity}");
            
            var product = _products.FirstOrDefault(p => p.Id == productId);
            if (product != null)
            {
                product.StockQuantity = newQuantity;
                product.UpdatedAt = DateTime.UtcNow;
            }
        }
        
        // Additional interface methods
        public async Task<Product> GetProductAsync(int productId)
        {
            return await GetProductByIdAsync(productId) ?? new Product();
        }
        
        public async Task<decimal> CalculateProductPriceAsync(int productId, string customerTier, DateTime date)
        {
            await Task.Delay(_random.Next(200, 500));
            Console.WriteLine($"[ProductService] Calculating price for product {productId}, tier {customerTier} on {date:yyyy-MM-dd}...");
            
            var product = _products.FirstOrDefault(p => p.Id == productId);
            if (product == null) return 0;
            
            var basePrice = product.Price;
            var tierMultiplier = customerTier.ToLower() switch
            {
                "premium" => 0.9m,
                "gold" => 0.85m,
                "platinum" => 0.8m,
                _ => 1.0m
            };
            
            return Math.Round(basePrice * tierMultiplier, 2);
        }
        
        public async Task<List<ProductCategory>> GetProductCategoriesAsync()
        {
            await Task.Delay(_random.Next(100, 300));
            Console.WriteLine($"[ProductService] Loading product categories...");
            
            return new List<ProductCategory>
            {
                new() { Id = 1, Name = "Electronics", Description = "Electronic devices and accessories", IsActive = true },
                new() { Id = 2, Name = "Books", Description = "Books and literature", IsActive = true },
                new() { Id = 3, Name = "Clothing", Description = "Apparel and fashion", IsActive = true },
                new() { Id = 4, Name = "Home", Description = "Home and garden items", IsActive = true },
                new() { Id = 5, Name = "Sports", Description = "Sports and outdoor equipment", IsActive = true }
            };
        }
        
        public async Task<ProductInventory> GetProductInventoryAsync(int productId)
        {
            await Task.Delay(_random.Next(50, 150));
            Console.WriteLine($"[ProductService] Getting inventory for product {productId}...");
            
            var product = _products.FirstOrDefault(p => p.Id == productId);
            return new ProductInventory
            {
                ProductId = productId,
                QuantityOnHand = product?.StockQuantity ?? 0,
                QuantityReserved = _random.Next(0, 10),
                LastUpdated = DateTime.UtcNow
            };
        }
        
        public async Task UpdateProductAsync(int productId, Product product)
        {
            // Simulate product update with Product object
            await Task.Delay(_random.Next(150, 300));
            
            Console.WriteLine($"[ProductService] Updating product {productId} with Product object");
            
            var existingProduct = _products.FirstOrDefault(p => p.Id == productId);
            if (existingProduct == null)
                throw new ArgumentException($"Product {productId} not found");

            existingProduct.Name = product.Name;
            existingProduct.Price = product.Price;
            existingProduct.Description = product.Description;
            existingProduct.UpdatedAt = DateTime.UtcNow;
        }
        
        public async Task UpdateInventoryAsync(int productId, int quantity)
        {
            await UpdateStockAsync(productId, quantity);
        }
        
        public async Task UpdateCategoryAsync(int categoryId, ProductCategory category)
        {
            await Task.Delay(_random.Next(100, 250));
            Console.WriteLine($"[ProductService] Updating category {categoryId}: {category.Name}");
        }
        
        public async Task<List<Product>> GetProductsByIdsAsync(int[] productIds)
        {
            await Task.Delay(_random.Next(100, 300));
            Console.WriteLine($"[ProductService] Getting products by IDs: {string.Join(", ", productIds)}");
            
            return _products.Where(p => productIds.Contains(p.Id)).ToList();
        }
        
        // Generic method temporarily commented out due to source generator limitations
        // public async Task<T> GetProductPropertyAsync<T>(int productId, string propertyName)
        // {
        //     await Task.Delay(_random.Next(50, 150));
        //     Console.WriteLine($"[ProductService] Getting property {propertyName} for product {productId}");
        //     
        //     var product = _products.FirstOrDefault(p => p.Id == productId);
        //     if (product?.Properties.TryGetValue(propertyName, out var value) == true && value is T typedValue)
        //         return typedValue;
        //         
        //     return default(T)!;
        // }
        
        public async Task<ProductStatistics> GetGlobalProductStatisticsAsync()
        {
            await Task.Delay(_random.Next(300, 600));
            Console.WriteLine($"[ProductService] Calculating global product statistics...");
            
            return new ProductStatistics
            {
                TotalProducts = _products.Count,
                ActiveProducts = _products.Count(p => p.IsActive),
                TotalCategories = 5,
                AveragePrice = _products.Any() ? _products.Average(p => p.Price) : 0,
                GeneratedAt = DateTime.UtcNow
            };
        }
        
        public async Task TrackProductViewAsync(int productId, int userId)
        {
            await Task.Delay(_random.Next(10, 50));
            Console.WriteLine($"[ProductService] Tracking view of product {productId} by user {userId}");
        }

        private List<Product> GenerateSampleProducts()
        {
            var products = new List<Product>();
            var categories = new[] { 1, 2, 3, 4, 5 }; // Electronics, Books, Clothing, Home, Sports
            var productNames = new[]
            {
                "Laptop Pro", "Wireless Mouse", "Programming Book", "T-Shirt", "Coffee Mug",
                "Smartphone", "Headphones", "Cookbook", "Jeans", "Desk Lamp",
                "Tablet", "Keyboard", "Novel", "Sweater", "Chair",
                "Monitor", "Webcam", "Magazine", "Shoes", "Plant Pot"
            };
            
            for (int i = 1; i <= 200; i++)
            {
                products.Add(new Product
                {
                    Id = i,
                    Name = productNames[_random.Next(productNames.Length)] + $" {i}",
                    Description = $"High-quality product {i} with excellent features and great value.",
                    Price = Math.Round((decimal)(_random.NextDouble() * 500 + 10), 2),
                    CategoryId = categories[_random.Next(categories.Length)],
                    StockQuantity = _random.Next(0, 100),
                    CreatedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 365)),
                    IsActive = _random.NextDouble() > 0.05
                });
            }
            
            return products;
        }
    }
}
