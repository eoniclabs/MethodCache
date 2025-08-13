using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace MethodCache.Core.Configuration
{
    public class ServiceConfiguration<T> : IServiceConfiguration<T>
    {
        private readonly Dictionary<string, CacheMethodSettings> _methodSettings;

        public ServiceConfiguration(Dictionary<string, CacheMethodSettings> methodSettings)
        {
            _methodSettings = methodSettings;
        }

        public IMethodConfiguration Method(Expression<Action<T>> methodExpression)
        {
            if (!(methodExpression.Body is MethodCallExpression methodCall))
            {
                throw new ArgumentException("Expression must be a method call.");
            }

            var methodInfo = methodCall.Method;
            // Replace + with . to match the expected format for nested types
            var methodId = $"{typeof(T).FullName?.Replace('+', '.')}.{methodInfo.Name}";

            Console.WriteLine($"ServiceConfiguration.Method: Configuring method {methodId}");

            if (!_methodSettings.TryGetValue(methodId, out var settings))
            {
                settings = new CacheMethodSettings();
                _methodSettings.Add(methodId, settings);
            }

            return new MethodConfiguration(settings);
        }
    }
}
