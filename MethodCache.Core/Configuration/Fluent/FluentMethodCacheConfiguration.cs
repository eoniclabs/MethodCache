using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Options;

namespace MethodCache.Core.Configuration.Fluent
{
    internal sealed class FluentMethodCacheConfiguration : IFluentMethodCacheConfiguration
    {
        private readonly List<Action<CacheEntryOptions.Builder>> _defaultPolicyBuilders = new();
        private readonly Dictionary<string, FluentGroupConfiguration> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<Type, FluentServiceConfiguration> _services = new();

        public IFluentMethodCacheConfiguration DefaultPolicy(Action<CacheEntryOptions.Builder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _defaultPolicyBuilders.Add(configure);
            return this;
        }

        public IFluentServiceConfiguration<TService> ForService<TService>()
        {
            var serviceType = typeof(TService);
            if (!_services.TryGetValue(serviceType, out var serviceConfiguration))
            {
                serviceConfiguration = new FluentServiceConfiguration(serviceType);
                _services.Add(serviceType, serviceConfiguration);
            }

            return new FluentServiceConfiguration<TService>(serviceConfiguration);
        }

        public IFluentGroupConfiguration ForGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Group name must be provided.", nameof(name));
            }

            if (!_groups.TryGetValue(name, out var groupConfiguration))
            {
                groupConfiguration = new FluentGroupConfiguration();
                _groups.Add(name, groupConfiguration);
            }

            return groupConfiguration;
        }

        internal void Apply(IMethodCacheConfiguration targetConfiguration)
        {
            if (targetConfiguration == null) throw new ArgumentNullException(nameof(targetConfiguration));

            var defaultOptions = BuildOptions(_defaultPolicyBuilders);
            if (defaultOptions?.Duration is TimeSpan duration)
            {
                targetConfiguration.DefaultDuration(duration);
            }

            foreach (var (groupName, groupConfiguration) in _groups)
            {
                var options = groupConfiguration.BuildOptions();
                var group = targetConfiguration.ForGroup(groupName);
                if (options.Duration.HasValue)
                {
                    group.Duration(options.Duration.Value);
                }

                foreach (var tag in options.Tags)
                {
                    group.TagWith(tag);
                }
            }

            foreach (var serviceConfiguration in _services.Values)
            {
                serviceConfiguration.Apply(targetConfiguration, defaultOptions);
            }
        }

        private static CacheEntryOptions? BuildOptions(IReadOnlyCollection<Action<CacheEntryOptions.Builder>> builders)
        {
            if (builders.Count == 0)
            {
                return null;
            }

            var builder = new CacheEntryOptions.Builder();
            foreach (var configure in builders)
            {
                configure(builder);
            }

            return builder.Build();
        }

        private sealed class FluentServiceConfiguration
        {
            private readonly Type _serviceType;
            private readonly Dictionary<string, FluentMethodConfiguration> _methods = new(StringComparer.Ordinal);

            public FluentServiceConfiguration(Type serviceType)
            {
                _serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            }

            public FluentMethodConfiguration GetOrCreate(MethodInfo methodInfo)
            {
                if (methodInfo == null) throw new ArgumentNullException(nameof(methodInfo));

                var methodKey = BuildMethodKey(_serviceType, methodInfo);
                if (!_methods.TryGetValue(methodKey, out var configuration))
                {
                    configuration = new FluentMethodConfiguration(methodKey);
                    _methods.Add(methodKey, configuration);
                }

                return configuration;
            }

            public void Apply(IMethodCacheConfiguration targetConfiguration, CacheEntryOptions? defaultOptions)
            {
                foreach (var (methodKey, methodConfiguration) in _methods)
                {
                    var options = methodConfiguration.BuildOptions(defaultOptions);
                    var settings = CacheEntryOptionsMapper.ToCacheMethodSettings(options);
                    targetConfiguration.AddMethod(methodKey, settings);
                    if (!string.IsNullOrWhiteSpace(methodConfiguration.GroupName))
                    {
                        targetConfiguration.SetMethodGroup(methodKey, methodConfiguration.GroupName);
                    }
                }
            }

            private static string BuildMethodKey(Type serviceType, MethodInfo methodInfo)
            {
                var typeName = serviceType.FullName ?? serviceType.Name;
                typeName = typeName.Replace('+', '.');
                return $"{typeName}.{methodInfo.Name}";
            }
        }

        private sealed class FluentServiceConfiguration<TService> : IFluentServiceConfiguration<TService>
        {
            private readonly FluentServiceConfiguration _inner;

            public FluentServiceConfiguration(FluentServiceConfiguration inner)
            {
                _inner = inner;
            }

            public IFluentMethodConfiguration Method(Expression<Action<TService>> method)
            {
                return CreateMethodConfiguration(method);
            }

            public IFluentMethodConfiguration Method<TResult>(Expression<Func<TService, TResult>> method)
            {
                return CreateMethodConfiguration(method);
            }

            public IFluentMethodConfiguration Method(Expression<Func<TService, Task>> method)
            {
                return CreateMethodConfiguration(method);
            }

            public IFluentMethodConfiguration Method<TResult>(Expression<Func<TService, Task<TResult>>> method)
            {
                return CreateMethodConfiguration(method);
            }

            private IFluentMethodConfiguration CreateMethodConfiguration(LambdaExpression expression)
            {
                var methodInfo = ExtractMethodInfo(expression);
                return _inner.GetOrCreate(methodInfo);
            }

            private static MethodInfo ExtractMethodInfo(LambdaExpression expression)
            {
                if (expression == null) throw new ArgumentNullException(nameof(expression));

                return expression.Body switch
                {
                    MethodCallExpression methodCall => methodCall.Method,
                    _ => throw new ArgumentException("Expression must target a method call.", nameof(expression))
                };
            }
        }

        private sealed class FluentMethodConfiguration : IFluentMethodConfiguration
        {
            private readonly string _methodKey;
            private readonly List<Action<CacheEntryOptions.Builder>> _configurations = new();
            private string? _groupName;

            public FluentMethodConfiguration(string methodKey)
            {
                _methodKey = methodKey;
            }

            public IFluentMethodConfiguration Configure(Action<CacheEntryOptions.Builder> configure)
            {
                if (configure == null) throw new ArgumentNullException(nameof(configure));
                _configurations.Add(configure);
                return this;
            }

            public IFluentMethodConfiguration WithGroup(string groupName)
            {
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    throw new ArgumentException("Group name must be provided.", nameof(groupName));
                }

                _groupName = groupName;
                return this;
            }

            public string? GroupName => _groupName;

            public CacheEntryOptions BuildOptions(CacheEntryOptions? defaultOptions)
            {
                var builder = new CacheEntryOptions.Builder();

                if (defaultOptions?.Duration is TimeSpan duration)
                {
                    builder.WithDuration(duration);
                }

                if (defaultOptions?.Tags.Count > 0)
                {
                    builder.WithTags(defaultOptions.Tags.ToArray());
                }

                foreach (var configure in _configurations)
                {
                    configure(builder);
                }

                return builder.Build();
            }
        }

        private sealed class FluentGroupConfiguration : IFluentGroupConfiguration
        {
            private readonly List<Action<CacheEntryOptions.Builder>> _configurations = new();

            public IFluentGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure)
            {
                if (configure == null) throw new ArgumentNullException(nameof(configure));

                _configurations.Add(configure);
                return this;
            }

            public CacheEntryOptions BuildOptions()
            {
                var builder = new CacheEntryOptions.Builder();
                foreach (var configure in _configurations)
                {
                    configure(builder);
                }

                return builder.Build();
            }
        }
    }
}
