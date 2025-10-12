using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;

namespace MethodCache.Core.Extensions
{
    /// <summary>
    /// Expression tree-based cache extensions that provide automatic key generation.
    /// </summary>
    public static class ExpressionBasedCacheExtensions
    {
        private static readonly ConcurrentDictionary<string, Delegate> CompiledFactories = new();
        private static readonly ConcurrentDictionary<string, string> GeneratedKeys = new();

        /// <summary>
        /// Gets a cached value or creates it using automatic key generation from the expression.
        /// </summary>
        /// <typeparam name="T">The type of the cached value.</typeparam>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="factoryExpression">Expression representing the factory method call.</param>
        /// <param name="configure">Optional configuration for cache entry options.</param>
        /// <param name="services">Optional service provider for dependency injection.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The cached or newly created value.</returns>
        /// <example>
        /// <code>
        /// var user = await cacheManager.GetOrCreateAsync(
        ///     () => userRepository.GetUserAsync(userId),
        ///     opts => opts.WithDuration(TimeSpan.FromHours(1))
        /// );
        /// </code>
        /// </example>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            Expression<Func<ValueTask<T>>> factoryExpression,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (factoryExpression == null) throw new ArgumentNullException(nameof(factoryExpression));

            var expressionKey = factoryExpression.ToString();

            // Generate cache key from expression
            var cacheKey = GeneratedKeys.GetOrAdd(expressionKey, _ =>
                GenerateKeyFromExpression(factoryExpression));

            // Get or compile the factory
            var compiledFactory = (Func<ValueTask<T>>)CompiledFactories.GetOrAdd(
                expressionKey,
                _ => factoryExpression.Compile());

            // Wrap in the standard factory format
            async ValueTask<T> Factory(CacheContext context, CancellationToken ct)
            {
                return await compiledFactory().ConfigureAwait(false);
            }

            // Use existing GetOrCreateAsync overload
            return await cacheManager.GetOrCreateAsync(cacheKey, Factory, configure, services, cancellationToken);
        }

        /// <summary>
        /// Gets a cached value or creates it using automatic key generation from the expression (synchronous version).
        /// </summary>
        /// <typeparam name="T">The type of the cached value.</typeparam>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="factoryExpression">Expression representing the factory method call.</param>
        /// <param name="configure">Optional configuration for cache entry options.</param>
        /// <param name="services">Optional service provider for dependency injection.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The cached or newly created value.</returns>
        /// <example>
        /// <code>
        /// var config = await cacheManager.GetOrCreateAsync(
        ///     () => configurationService.GetSettings(),
        ///     opts => opts.WithDuration(TimeSpan.FromMinutes(30))
        /// );
        /// </code>
        /// </example>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            Expression<Func<T>> factoryExpression,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (factoryExpression == null) throw new ArgumentNullException(nameof(factoryExpression));

            var expressionKey = factoryExpression.ToString();

            // Generate cache key from expression
            var cacheKey = GeneratedKeys.GetOrAdd(expressionKey, _ =>
                GenerateKeyFromExpression(factoryExpression));

            // Get or compile the factory
            var compiledFactory = (Func<T>)CompiledFactories.GetOrAdd(
                expressionKey,
                _ => factoryExpression.Compile());

            // Wrap in the standard factory format
            ValueTask<T> Factory(CacheContext context, CancellationToken ct)
            {
                var result = compiledFactory();
                return new ValueTask<T>(result);
            }

            // Use existing GetOrCreateAsync overload
            return await cacheManager.GetOrCreateAsync(cacheKey, Factory, configure, services, cancellationToken);
        }

        /// <summary>
        /// Gets a cached value or creates it using automatic key generation with custom key generator.
        /// </summary>
        /// <typeparam name="T">The type of the cached value.</typeparam>
        /// <param name="cacheManager">The cache manager.</param>
        /// <param name="factoryExpression">Expression representing the factory method call.</param>
        /// <param name="keyGenerator">Custom key generator for advanced scenarios.</param>
        /// <param name="configure">Optional configuration for cache entry options.</param>
        /// <param name="services">Optional service provider for dependency injection.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The cached or newly created value.</returns>
        public static async ValueTask<T> GetOrCreateAsync<T>(
            this ICacheManager cacheManager,
            Expression<Func<ValueTask<T>>> factoryExpression,
            IExpressionKeyGenerator keyGenerator,
            Action<CacheEntryOptions.Builder>? configure = null,
            IServiceProvider? services = null,
            CancellationToken cancellationToken = default)
        {
            if (cacheManager == null) throw new ArgumentNullException(nameof(cacheManager));
            if (factoryExpression == null) throw new ArgumentNullException(nameof(factoryExpression));
            if (keyGenerator == null) throw new ArgumentNullException(nameof(keyGenerator));

            var expressionKey = factoryExpression.ToString();

            // Generate cache key using custom generator
            var cacheKey = keyGenerator.GenerateKey(factoryExpression);

            // Get or compile the factory
            var compiledFactory = (Func<ValueTask<T>>)CompiledFactories.GetOrAdd(
                expressionKey,
                _ => factoryExpression.Compile());

            // Wrap in the standard factory format
            async ValueTask<T> Factory(CacheContext context, CancellationToken ct)
            {
                return await compiledFactory().ConfigureAwait(false);
            }

            // Use existing GetOrCreateAsync overload
            return await cacheManager.GetOrCreateAsync(cacheKey, Factory, configure, services, cancellationToken);
        }

        private static string GenerateKeyFromExpression(Expression expression)
        {
            if (expression is LambdaExpression lambda && lambda.Body is MethodCallExpression methodCall)
            {
                return GenerateKeyFromMethodCall(methodCall);
            }

            throw new ArgumentException("Expression must be a lambda with a method call body", nameof(expression));
        }

        private static string GenerateKeyFromMethodCall(MethodCallExpression methodCall)
        {
            var keyBuilder = new StringBuilder();

            // Add declaring type name
            if (methodCall.Method.DeclaringType != null)
            {
                keyBuilder.Append(methodCall.Method.DeclaringType.Name);
                keyBuilder.Append('.');
            }

            // Add method name
            keyBuilder.Append(methodCall.Method.Name);

            // Add parameters
            var parameters = methodCall.Method.GetParameters();
            for (int i = 0; i < methodCall.Arguments.Count; i++)
            {
                var paramName = parameters[i].Name ?? $"arg{i}";
                var paramValue = ExtractParameterValue(methodCall.Arguments[i]);

                keyBuilder.Append(':');
                keyBuilder.Append(paramName);
                keyBuilder.Append(':');
                keyBuilder.Append(SerializeParameterValue(paramValue));
            }

            return keyBuilder.ToString();
        }

        private static object? ExtractParameterValue(Expression argument)
        {
            try
            {
                // Handle constant expressions directly
                if (argument is ConstantExpression constant)
                {
                    return constant.Value;
                }

                // Handle member access (field/property access)
                if (argument is MemberExpression member)
                {
                    return GetMemberValue(member);
                }

                // For other expressions, compile and execute
                var lambda = Expression.Lambda(argument);
                var compiled = lambda.Compile();
                return compiled.DynamicInvoke();
            }
            catch
            {
                // Fallback to expression string representation
                return argument.ToString();
            }
        }

        private static object? GetMemberValue(MemberExpression member)
        {
            // Handle static members
            if (member.Expression == null)
            {
                return member.Member switch
                {
                    FieldInfo field => field.GetValue(null),
                    PropertyInfo property => property.GetValue(null),
                    _ => member.ToString()
                };
            }

            // Handle instance members
            var instance = ExtractParameterValue(member.Expression);
            return member.Member switch
            {
                FieldInfo field => field.GetValue(instance),
                PropertyInfo property => property.GetValue(instance),
                _ => member.ToString()
            };
        }

        private static string SerializeParameterValue(object? value)
        {
            if (value == null) return "null";

            return value switch
            {
                string s => EscapeString(s),
                int i => i.ToString(),
                long l => l.ToString(),
                bool b => b.ToString().ToLowerInvariant(),
                DateTime dt => dt.ToBinary().ToString(),
                DateTimeOffset dto => dto.ToUnixTimeMilliseconds().ToString(),
                Guid guid => guid.ToString("N"),
                Enum e => $"{e.GetType().Name}.{e}",
                _ => EscapeString(value.ToString() ?? "unknown")
            };
        }

        private static string EscapeString(string input)
        {
            // Escape colons and other special characters that might conflict with key format
            return input.Replace(":", "__COLON__")
                       .Replace("|", "__PIPE__")
                       .Replace("\n", "__NEWLINE__")
                       .Replace("\r", "__RETURN__");
        }
    }

    /// <summary>
    /// Interface for custom expression-based key generators.
    /// </summary>
    public interface IExpressionKeyGenerator
    {
        /// <summary>
        /// Generates a cache key from the given expression.
        /// </summary>
        /// <param name="expression">The expression to analyze.</param>
        /// <returns>A unique cache key.</returns>
        string GenerateKey(Expression expression);
    }

    /// <summary>
    /// Hash-based expression key generator for scenarios with very long parameter lists.
    /// </summary>
    public class HashBasedExpressionKeyGenerator : IExpressionKeyGenerator
    {
        private readonly ICacheKeyGenerator _baseKeyGenerator;

        public HashBasedExpressionKeyGenerator(ICacheKeyGenerator baseKeyGenerator)
        {
            _baseKeyGenerator = baseKeyGenerator ?? throw new ArgumentNullException(nameof(baseKeyGenerator));
        }

        public string GenerateKey(Expression expression)
        {
            // Generate the default key first
            var defaultKey = GenerateDefaultKey(expression);

            // If it's too long, hash it using the base key generator
            if (defaultKey.Length > 200)
            {
                var policy = CacheRuntimePolicy.Empty("ExpressionHash");
                return _baseKeyGenerator.GenerateKey("ExpressionHash", new object[] { defaultKey }, policy);
            }

            return defaultKey;
        }

        private static string GenerateDefaultKey(Expression expression)
        {
            if (expression is LambdaExpression lambda && lambda.Body is MethodCallExpression methodCall)
            {
                var keyBuilder = new StringBuilder();

                if (methodCall.Method.DeclaringType != null)
                {
                    keyBuilder.Append(methodCall.Method.DeclaringType.Name);
                    keyBuilder.Append('.');
                }

                keyBuilder.Append(methodCall.Method.Name);
                keyBuilder.Append(':');
                keyBuilder.Append(methodCall.Arguments.Count);

                return keyBuilder.ToString();
            }

            return expression.ToString();
        }
    }
}