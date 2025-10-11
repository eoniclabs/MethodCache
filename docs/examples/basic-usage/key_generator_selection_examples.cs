using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Extensions;
using MethodCache.Core.KeyGenerators;

namespace MethodCache.Examples
{
    /// <summary>
    /// Examples showing how to choose different key generators in the fluent API.
    /// </summary>
    public class KeyGeneratorSelectionExamples
    {
        private readonly ICacheManager _cache;
        private readonly IUserRepository _userRepo;
        private readonly IDataService _dataService;

        public KeyGeneratorSelectionExamples(
            ICacheManager cache,
            IUserRepository userRepo,
            IDataService dataService)
        {
            _cache = cache;
            _userRepo = userRepo;
            _dataService = dataService;
        }

        /// <summary>
        /// Default behavior - uses FastHashKeyGenerator from DI or creates one
        /// </summary>
        public async Task<User> GetUser_Default(int userId)
        {
            return await _cache.GetOrCreateAsync(
                () => _userRepo.GetUserAsync(userId),
                new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) }
            );
            // Uses FastHashKeyGenerator by default
            // Key: "GetUserAsync_a1b2c3d4e5f6g7h8" (FNV hash)
        }

        /// <summary>
        /// Explicit FastHashKeyGenerator - for high performance scenarios
        /// </summary>
        public async Task<User> GetUser_FastHash(int userId)
        {
            return await _cache.GetOrCreateAsync(
                () => _userRepo.GetUserAsync(userId),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) },
                keyGenerator: new FastHashKeyGenerator()
            );
            // Key: "GetUserAsync_a1b2c3d4e5f6g7h8" (collision-resistant FNV hash)
        }

        /// <summary>
        /// JsonKeyGenerator - for debugging and human-readable keys
        /// </summary>
        public async Task<User> GetUser_Json(int userId, bool includeProfile)
        {
            return await _cache.GetOrCreateAsync(
                () => _userRepo.GetUserWithProfileAsync(userId, includeProfile),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) },
                keyGenerator: new JsonKeyGenerator()
            );
            // Key: "GetUserWithProfileAsync:userId:123:includeProfile:true" (human-readable)
        }

        /// <summary>
        /// MessagePackKeyGenerator - for complex objects and binary efficiency
        /// </summary>
        public async Task<AnalyticsReport> GetReport_MessagePack(ReportCriteria criteria)
        {
            return await _cache.GetOrCreateAsync(
                () => _dataService.GenerateReportAsync(criteria),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(2) },
                keyGenerator: new MessagePackKeyGenerator()
            );
            // Key: "GenerateReportAsync_[binary_hash]" (efficient for complex objects)
        }

        /// <summary>
        /// Different generators for different scenarios in the same class
        /// </summary>
        public class OptimalKeyGeneratorUsage
        {
            private readonly ICacheManager _cache;
            private readonly IDataService _dataService;

            // Pre-create instances for better performance
            private static readonly FastHashKeyGenerator FastHashGenerator = new();
            private static readonly JsonKeyGenerator JsonGenerator = new();
            private static readonly MessagePackKeyGenerator MessagePackGenerator = new();

            public OptimalKeyGeneratorUsage(ICacheManager cache, IDataService dataService)
            {
                _cache = cache;
                _dataService = dataService;
            }

            /// <summary>
            /// Simple parameters - use FastHashKeyGenerator for performance
            /// </summary>
            public async Task<UserSettings> GetUserSettings(int userId, string category)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.GetUserSettingsAsync(userId, category),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(30) },
                    keyGenerator: FastHashGenerator
                );
            }

            /// <summary>
            /// Development/debugging - use JsonKeyGenerator for readability
            /// </summary>
            public async Task<ConfigData> GetConfigForDebugging(string environment, string feature)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.GetConfigDataAsync(environment, feature),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(15) },
                    keyGenerator: JsonGenerator
                );
                // Produces: "GetConfigDataAsync:environment:prod:feature:newUi"
            }

            /// <summary>
            /// Complex object - use MessagePackKeyGenerator for efficient serialization
            /// </summary>
            public async Task<ProcessingResult> ProcessComplexData(
                ProcessingRequest request,
                ProcessingOptions options,
                List<string> tags)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.ProcessDataAsync(request, options, tags),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) },
                    keyGenerator: MessagePackGenerator
                );
                // Efficiently handles complex nested objects
            }
        }

        /// <summary>
        /// Conditional key generator selection based on data characteristics
        /// </summary>
        public async Task<DataResult> GetData_ConditionalKeyGenerator(QueryRequest request)
        {
            ICacheKeyGenerator keyGenerator;

            // Choose optimal key generator based on request complexity
            if (request.Parameters.Count > 10 || request.HasComplexObjects)
            {
                // Complex requests: Use MessagePack for binary efficiency
                keyGenerator = new MessagePackKeyGenerator();
            }
            else if (request.IsDebugMode)
            {
                // Debug mode: Use JSON for human readability
                keyGenerator = new JsonKeyGenerator();
            }
            else
            {
                // Normal requests: Use FastHash for performance
                keyGenerator = new FastHashKeyGenerator();
            }

            return await _cache.GetOrCreateAsync(
                () => _dataService.ExecuteQueryAsync(request),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromMinutes(30) },
                keyGenerator: keyGenerator
            );
        }

        /// <summary>
        /// Custom key generator for specialized scenarios
        /// </summary>
        public async Task<TenantData> GetTenantData_Custom(string tenantId, string dataType)
        {
            var tenantKeyGenerator = new TenantAwareKeyGenerator();

            return await _cache.GetOrCreateAsync(
                () => _dataService.GetTenantDataAsync(tenantId, dataType),
                settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(1) },
                keyGenerator: tenantKeyGenerator
            );
            // Key: "tenant:acme-corp:GetTenantDataAsync_hash"
        }

        /// <summary>
        /// Performance comparison: Key generator impact
        /// </summary>
        public class PerformanceComparison
        {
            private readonly ICacheManager _cache;
            private readonly IDataService _dataService;

            public PerformanceComparison(ICacheManager cache, IDataService dataService)
            {
                _cache = cache;
                _dataService = dataService;
            }

            // Hot path - use FastHashKeyGenerator for maximum performance
            public async Task<CriticalData> GetCriticalData_HighPerformance(int id)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.GetCriticalDataAsync(id),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromSeconds(30) },
                    keyGenerator: new FastHashKeyGenerator()
                );
                // Optimized for speed and collision resistance
            }

            // Development - use JsonKeyGenerator for easier debugging
            public async Task<CriticalData> GetCriticalData_Development(int id)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.GetCriticalDataAsync(id),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromSeconds(30) },
                    keyGenerator: new JsonKeyGenerator()
                );
                // Human-readable keys: "GetCriticalDataAsync:id:123"
            }

            // Complex data - use MessagePackKeyGenerator for size efficiency
            public async Task<LargeReport> GetLargeReport_Efficient(ComplexCriteria criteria)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.GenerateLargeReportAsync(criteria),
                    settings: new CacheRuntimeDescriptor { Duration = TimeSpan.FromHours(4) },
                    keyGenerator: new MessagePackKeyGenerator()
                );
                // Binary serialization handles large objects efficiently
            }
        }

        /// <summary>
        /// Showing all API tiers with key generator support
        /// </summary>
        public class AllApiTiers
        {
            private readonly ICacheManager _cache;
            private readonly IUserRepository _userRepo;

            public AllApiTiers(ICacheManager cache, IUserRepository userRepo)
            {
                _cache = cache;
                _userRepo = userRepo;
            }

            // Tier 1: Factory only (uses default or DI-configured generator)
            public async Task<User> Tier1_FactoryOnly(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    () => _userRepo.GetUserAsync(userId)
                );
            }

            // Tier 1b: Factory + explicit key generator
            public async Task<User> Tier1b_FactoryWithGenerator(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    () => _userRepo.GetUserAsync(userId),
                    settings: new CacheRuntimeDescriptor(),
                    keyGenerator: new JsonKeyGenerator()
                );
            }

            // Tier 2: Method + args (uses default or DI-configured generator)
            public async Task<User> Tier2_MethodArgs(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    nameof(_userRepo.GetUserAsync),
                    new object[] { userId },
                    () => _userRepo.GetUserAsync(userId),
                    new CacheRuntimeDescriptor(),
                    new FastHashKeyGenerator(),
                    true
                );
            }

            // Tier 2b: Method + args + explicit key generator
            public async Task<User> Tier2b_MethodArgsWithGenerator(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    nameof(_userRepo.GetUserAsync),
                    new object[] { userId },
                    () => _userRepo.GetUserAsync(userId),
                    new CacheRuntimeDescriptor(),
                    new FastHashKeyGenerator(),
                    true
                );
            }

            // Tier 3: Manual key (unchanged)
            public async Task<User> Tier3_ManualKey(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    key: $"user:{userId}",
                    factory: () => _userRepo.GetUserAsync(userId),
                    settings: new CacheRuntimeDescriptor()
                );
            }
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

            // Generate base key
            var baseKey = _baseGenerator.GenerateKey(methodName, args, descriptor);

            // Prepend tenant for isolation
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
        Task<User> GetUserWithProfileAsync(int userId, bool includeProfile);
    }

    public interface IDataService
    {
        Task<AnalyticsReport> GenerateReportAsync(ReportCriteria criteria);
        Task<UserSettings> GetUserSettingsAsync(int userId, string category);
        Task<ConfigData> GetConfigDataAsync(string environment, string feature);
        Task<ProcessingResult> ProcessDataAsync(ProcessingRequest request, ProcessingOptions options, List<string> tags);
        Task<DataResult> ExecuteQueryAsync(QueryRequest request);
        Task<TenantData> GetTenantDataAsync(string tenantId, string dataType);
        Task<CriticalData> GetCriticalDataAsync(int id);
        Task<LargeReport> GenerateLargeReportAsync(ComplexCriteria criteria);
    }

    public class User { }
    public class AnalyticsReport { }
    public class ReportCriteria { }
    public class UserSettings { }
    public class ConfigData { }
    public class ProcessingResult { }
    public class ProcessingRequest { }
    public class ProcessingOptions { }
    public class DataResult { }
    public class TenantData { }
    public class CriticalData { }
    public class LargeReport { }
    public class ComplexCriteria { }

    public class QueryRequest
    {
        public Dictionary<string, object> Parameters { get; set; } = new();
        public bool HasComplexObjects { get; set; }
        public bool IsDebugMode { get; set; }
    }
}