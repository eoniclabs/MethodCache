using MethodCache.Core;
using MethodCache.Core.Runtime;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MethodCache.SampleApp.Infrastructure
{
    /// <summary>
    /// Custom key generator that demonstrates advanced key generation strategies
    /// </summary>
    public class CustomKeyGenerator : ICacheKeyGenerator
    {
        private readonly JsonSerializerOptions _jsonOptions;

        public CustomKeyGenerator()
        {
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
        {
            var keyComponents = new StringBuilder();
            
            // Method name as base
            keyComponents.Append($"method:{methodName}");

            // Add version if specified
            if (descriptor.Version.HasValue)
            {
                keyComponents.Append($"_v:{descriptor.Version}");
            }

            // Process arguments
            if (args.Length > 0)
            {
                keyComponents.Append("_args:");

                foreach (var arg in args)
                {
                    var argKey = GenerateArgumentKey(arg);
                    keyComponents.Append($"{argKey}_");
                }

                // Remove trailing underscore
                keyComponents.Length--;
            }

            // Add tags for additional context
            if (descriptor.Tags?.Count > 0)
            {
                var sortedTags = string.Join(",", descriptor.Tags.OrderBy(t => t));
                keyComponents.Append($"_tags:{sortedTags}");
            }

            var rawKey = keyComponents.ToString();
            
            // Generate hash for consistent length and avoid key collision
            var hash = GenerateHash(rawKey);
            
            // Create final key with readable prefix and hash
            var finalKey = $"cache:{methodName.Split('.').LastOrDefault()}:{hash}";
            
            Console.WriteLine($"[KEY GENERATOR] {methodName} -> {finalKey}");
            
            return finalKey;
        }

        private string GenerateArgumentKey(object? arg)
        {
            if (arg == null)
                return "null";

            // Handle ICacheKeyProvider for custom key generation
            if (arg is ICacheKeyProvider cacheKeyProvider)
            {
                return cacheKeyProvider.CacheKeyPart;
            }

            // Handle primitive types efficiently
            return arg switch
            {
                string str => $"str:{str}",
                int i => $"int:{i}",
                long l => $"long:{l}",
                decimal d => $"dec:{d}",
                double db => $"dbl:{db:F6}", // Fixed precision for consistency
                float f => $"flt:{f:F6}",
                bool b => $"bool:{b}",
                DateTime dt => $"dt:{dt:yyyy-MM-ddTHH:mm:ss.fffZ}",
                DateTimeOffset dto => $"dto:{dto:yyyy-MM-ddTHH:mm:ss.fffZ}",
                Guid guid => $"guid:{guid}",
                Enum enumValue => $"enum:{enumValue.GetType().Name}.{enumValue}",
                
                // Handle arrays and collections
                System.Collections.IEnumerable enumerable => GenerateCollectionKey(enumerable),
                
                // Complex objects - serialize to JSON
                _ => GenerateComplexObjectKey(arg)
            };
        }

        private string GenerateCollectionKey(System.Collections.IEnumerable enumerable)
        {
            var items = new List<string>();
            
            foreach (var item in enumerable)
            {
                items.Add(GenerateArgumentKey(item));
            }

            // Sort for consistent ordering
            items.Sort();
            
            return $"collection:[{string.Join(",", items)}]";
        }

        private string GenerateComplexObjectKey(object obj)
        {
            try
            {
                // Serialize to JSON for consistent representation
                var json = JsonSerializer.Serialize(obj, _jsonOptions);
                
                // For very large objects, use hash to keep key manageable
                if (json.Length > 500)
                {
                    var hash = GenerateHash(json);
                    return $"complex:{obj.GetType().Name}:{hash}";
                }
                
                return $"json:{json}";
            }
            catch (Exception ex)
            {
                // Fallback to type and hash code if serialization fails
                var fallback = $"obj:{obj.GetType().Name}:{obj.GetHashCode()}";
                Console.WriteLine($"[KEY GENERATOR WARNING] Failed to serialize {obj.GetType().Name}: {ex.Message}, using fallback: {fallback}");
                return fallback;
            }
        }

        private static string GenerateHash(string input)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            
            // Convert to base64 and make URL-safe
            return Convert.ToBase64String(hashBytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }

    /// <summary>
    /// Specialized key generator for frequently accessed methods that prioritizes performance
    /// </summary>
    public class HighPerformanceKeyGenerator : ICacheKeyGenerator
    {
        private readonly ConcurrentCache<string, string> _keyCache = new();

        public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
        {
            // For methods with no arguments, cache the key
            if (args.Length == 0)
            {
                var cacheKey = $"{methodName}_v{descriptor.Version ?? 0}";
                return _keyCache.GetOrAdd(cacheKey, key => $"fast:{methodName}:v{descriptor.Version ?? 0}:noargs");
            }

            // For simple argument patterns, use optimized path
            if (args.Length == 1 && IsSimpleType(args[0]))
            {
                return $"fast:{methodName}:v{descriptor.Version ?? 0}:{args[0]}";
            }

            // Fall back to more complex key generation for complex arguments
            var keyBuilder = new StringBuilder($"fast:{methodName}:v{descriptor.Version ?? 0}:");
            
            foreach (var arg in args)
            {
                keyBuilder.Append(GetSimpleKey(arg));
                keyBuilder.Append('_');
            }
            
            // Remove trailing underscore
            if (keyBuilder.Length > 0 && keyBuilder[^1] == '_')
                keyBuilder.Length--;

            return keyBuilder.ToString();
        }

        private static bool IsSimpleType(object? obj)
        {
            return obj is string or int or long or bool or decimal or DateTime or Guid;
        }

        private static string GetSimpleKey(object? obj)
        {
            return obj switch
            {
                null => "null",
                string s => s,
                int i => i.ToString(),
                long l => l.ToString(),
                bool b => b.ToString().ToLower(),
                decimal d => d.ToString("F6"),
                DateTime dt => dt.ToString("yyyyMMddHHmmss"),
                Guid g => g.ToString("N"),
                _ => obj.GetHashCode().ToString()
            };
        }
    }

    /// <summary>
    /// Simple concurrent cache helper
    /// </summary>
    public class ConcurrentCache<TKey, TValue> where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, TValue> _cache = new();

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            return _cache.GetOrAdd(key, valueFactory);
        }

        public void Clear() => _cache.Clear();
    }
}