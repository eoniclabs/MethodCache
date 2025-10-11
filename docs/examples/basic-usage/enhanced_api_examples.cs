using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Extensions;
using MethodCache.Core.KeyGenerators;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Examples
{
    /// <summary>
    /// Examples showing the enhanced fluent API that builds on existing key generators.
    /// </summary>
    public class EnhancedApiExamples
    {
        private readonly ICacheManager _cacheManager;
        private readonly IUserRepository _userRepository;
        private readonly IOrderService _orderService;
        private readonly IServiceProvider _services;

        public EnhancedApiExamples(
            ICacheManager cacheManager,
            IUserRepository userRepository,
            IOrderService orderService,
            IServiceProvider services)
        {
            _cacheManager = cacheManager;
            _userRepository = userRepository;
            _orderService = orderService;
            _services = services;
        }

        /// <summary>
        /// Enhanced API: Method name + args (leverages existing FastHashKeyGenerator)
        /// </summary>
        public async Task<User> GetUserEnhanced(int userId)
        {
            return await _cacheManager.GetOrCreateAsync(
                methodName: nameof(_userRepository.GetUserAsync),
                args: new object[] { userId },
                factory: () => _userRepository.GetUserAsync(userId),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) },
                keyGenerator: new FastHashKeyGenerator(),
                requireIdempotent: true
            );
            // Uses FastHashKeyGenerator by default
            // Generated key: Efficient hash of "GetUserAsync" + userId
        }

        /// <summary>
        /// Enhanced API with explicit key generator selection
        /// </summary>
        public async Task<ComplexReport> GetComplexReport(ReportCriteria criteria)
        {
            // For complex objects, explicitly use MessagePackKeyGenerator
            var messagePackGenerator = new MessagePackKeyGenerator();
            var cacheKey = messagePackGenerator.GenerateKey(
                nameof(_orderService.GenerateReportAsync),
                new object[] { criteria },
                new CacheRuntimeDescriptor()
            );

            return await _cacheManager.GetOrCreateAsync(
                key: cacheKey,
                factory: () => _orderService.GenerateReportAsync(criteria),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(2), Tags = new[] { "reports", "analytics" } }
            );
        }

        /// <summary>
        /// Different key generators for different scenarios
        /// </summary>
        public async Task<List<Order>> GetOrdersWithAppropriateKeyGenerator(
            int customerId,
            OrderStatus status,
            DateTime from,
            DateTime to)
        {
            // Use JsonKeyGenerator for simpler types
            var jsonGenerator = new JsonKeyGenerator();
            var methodName = $"{nameof(_orderService)}.{nameof(_orderService.GetOrdersByCustomerAsync)}";
            var args = new object[] { customerId, status, from, to };

            return await _cacheManager.GetOrCreateAsync(
                methodName: methodName,
                args: args,
                factory: () => _orderService.GetOrdersByCustomerAsync(customerId, status, from, to),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(30), Tags = new[] { "orders", $"customer:{customerId}" } },
                keyGenerator: jsonGenerator,
                requireIdempotent: true
            );
        }

        /// <summary>
        /// Showing backward compatibility - manual keys still work perfectly
        /// </summary>
        public async Task<AppSettings> GetSettingsManualKey(string section)
        {
            // Existing manual key approach unchanged
            return await _cacheManager.GetOrCreateAsync(
                key: $"settings:{section}",
                factory: () => GetSettingsFromConfig(section),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(15) }
            );
        }

        /// <summary>
        /// Performance comparison: Manual vs Enhanced API
        /// </summary>
        public class PerformanceComparison
        {
            private readonly ICacheManager _cache;
            private readonly IUserRepository _userRepo;
            private readonly IServiceProvider _services;

            public PerformanceComparison(ICacheManager cache, IUserRepository userRepo, IServiceProvider services)
            {
                _cache = cache;
                _userRepo = userRepo;
                _services = services;
            }

            // Original approach: Manual key construction
            public async Task<User> GetUser_Manual(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    key: $"user:{userId}",  // Manual string interpolation
                    factory: () => _userRepo.GetUserAsync(userId),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) }
                );
            }

            // Enhanced approach: Leverages FastHashKeyGenerator
            public async Task<User> GetUser_Enhanced(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    methodName: nameof(_userRepo.GetUserAsync),
                    args: new object[] { userId },
                    factory: () => _userRepo.GetUserAsync(userId),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) },
                    keyGenerator: new FastHashKeyGenerator(),
                    requireIdempotent: true
                );
                // FastHashKeyGenerator produces collision-resistant hash
                // Better for cache distribution and performance
            }

            // Complex scenario: Multiple parameters
            public async Task<List<Order>> GetOrders_Manual(int customerId, OrderStatus status, bool includeItems)
            {
                // Error-prone manual key construction
                var key = $"orders:customer:{customerId}:status:{status}:items:{includeItems}";

                return await _cache.GetOrCreateAsync(
                    key: key,
                    factory: () => _userRepo.GetOrdersAsync(customerId, status, includeItems),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(30) }
                );
            }

            public async Task<List<Order>> GetOrders_Enhanced(int customerId, OrderStatus status, bool includeItems)
            {
                // FastHashKeyGenerator handles complex parameter serialization
                return await _cache.GetOrCreateAsync(
                    methodName: "GetOrdersAsync",
                    args: new object[] { customerId, status, includeItems },
                    factory: () => _userRepo.GetOrdersAsync(customerId, status, includeItems),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(30) },
                    keyGenerator: new FastHashKeyGenerator(),
                    requireIdempotent: true
                );
                // Automatic handling of enum serialization, null values, etc.
            }
        }

        /// <summary>
        /// Advanced scenarios with custom key generators
        /// </summary>
        public class AdvancedKeyGeneratorScenarios
        {
            private readonly ICacheManager _cache;
            private readonly IDataService _dataService;

            public AdvancedKeyGeneratorScenarios(ICacheManager cache, IDataService dataService)
            {
                _cache = cache;
                _dataService = dataService;
            }

            /// <summary>
            /// Use different key generators based on data characteristics
            /// </summary>
            public async Task<DataResult> GetDataWithOptimalKeyGenerator(QueryRequest request)
            {
                ICacheKeyGenerator keyGenerator;

                // Choose key generator based on request characteristics
                if (request.Parameters.Count > 10)
                {
                    // Complex requests: Use MessagePack for binary efficiency
                    keyGenerator = new MessagePackKeyGenerator();
                }
                else if (request.Parameters.Values.Any(v => v is string && ((string)v).Length > 100))
                {
                    // Large strings: Use FastHashKeyGenerator for hashing
                    keyGenerator = new FastHashKeyGenerator();
                }
                else
                {
                    // Simple requests: Use JSON for debuggability
                    keyGenerator = new JsonKeyGenerator();
                }

                var cacheKey = keyGenerator.GenerateKey(
                    nameof(_dataService.QueryDataAsync),
                    new object[] { request },
                    new CacheRuntimeDescriptor()
                );

                return await _cache.GetOrCreateAsync(
                    key: cacheKey,
                    factory: () => _dataService.QueryDataAsync(request),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(30) }
                );
            }

            /// <summary>
            /// Custom key generator for tenant-aware scenarios
            /// </summary>
            public async Task<TenantData> GetTenantData(string tenantId, string dataType)
            {
                var tenantKeyGenerator = new TenantAwareKeyGenerator();

                return await _cache.GetOrCreateAsync(
                    methodName: nameof(_dataService.GetTenantDataAsync),
                    args: new object[] { tenantId, dataType },
                    factory: () => _dataService.GetTenantDataAsync(tenantId, dataType),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) },
                    keyGenerator: tenantKeyGenerator,
                    requireIdempotent: true
                );
            }
        }

        private ValueTask<AppSettings> GetSettingsFromConfig(string section)
        {
            // Dummy implementation
            return new ValueTask<AppSettings>(new AppSettings());
        }
    }

    /// <summary>
    /// Custom key generator that adds tenant context
    /// </summary>
    public class TenantAwareKeyGenerator : ICacheKeyGenerator
    {
        private readonly FastHashKeyGenerator _baseGenerator = new();

        public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
        {
            // Get tenant from current context (HTTP context, etc.)
            var tenantId = GetCurrentTenantId();

            // Prepend tenant to ensure isolation
            var baseKey = _baseGenerator.GenerateKey(methodName, args, descriptor);
            return $"tenant:{tenantId}:{baseKey}";
        }

        private string GetCurrentTenantId()
        {
            // Implementation would get tenant from HTTP context, JWT, etc.
            return "default-tenant";
        }
    }

    // Supporting interfaces and types
    public interface IUserRepository
    {
        Task<User> GetUserAsync(int userId);
        Task<List<Order>> GetOrdersAsync(int customerId, OrderStatus status, bool includeItems);
    }

    public interface IOrderService
    {
        Task<List<Order>> GetOrdersByCustomerAsync(int customerId, OrderStatus status, DateTime from, DateTime to);
        Task<ComplexReport> GenerateReportAsync(ReportCriteria criteria);
    }

    public interface IDataService
    {
        Task<DataResult> QueryDataAsync(QueryRequest request);
        Task<TenantData> GetTenantDataAsync(string tenantId, string dataType);
    }

    public class User { }
    public class Order { }
    public class AppSettings { }
    public class ComplexReport { }
    public class ReportCriteria { }
    public class DataResult { }
    public class TenantData { }
    public class QueryRequest
    {
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public enum OrderStatus
    {
        Pending,
        Active,
        Completed
    }
}