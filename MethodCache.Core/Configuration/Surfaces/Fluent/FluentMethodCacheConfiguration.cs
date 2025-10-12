using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Metrics;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;

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

        internal IReadOnlyList<PolicyDraft> BuildMethodPolicies()
        {
            var drafts = new List<PolicyDraft>();
            var defaultOptions = BuildOptions(_defaultPolicyBuilders);
            var groupOptions = new Dictionary<string, CacheEntryOptions>(StringComparer.Ordinal);

            foreach (var (groupName, groupConfiguration) in _groups)
            {
                groupOptions[groupName] = groupConfiguration.BuildOptions();
            }

            foreach (var serviceConfiguration in _services.Values)
            {
                foreach (var (methodKey, methodConfiguration) in serviceConfiguration.Methods)
                {
                    CacheEntryOptions? groupOption = null;
                    if (!string.IsNullOrWhiteSpace(methodConfiguration.GroupName) &&
                        groupOptions.TryGetValue(methodConfiguration.GroupName, out var groupSettings))
                    {
                        groupOption = groupSettings;
                    }

                    var options = methodConfiguration.BuildOptions(defaultOptions, groupOption);
                    var policyBuilder = CachePolicyBuilderFactory.FromOptions(options);

                    if (methodConfiguration.IsIdempotent.HasValue)
                    {
                        policyBuilder.RequireIdempotent(methodConfiguration.IsIdempotent.Value);
                    }

                    if (!string.IsNullOrWhiteSpace(methodConfiguration.GroupName))
                    {
                        policyBuilder.AddMetadata("group", methodConfiguration.GroupName);
                    }

                    drafts.Add(policyBuilder.Build(methodKey));
                }
            }

            return drafts;
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

            public IReadOnlyDictionary<string, FluentMethodConfiguration> Methods => _methods;

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
            private bool? _isIdempotent;

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
            public bool? IsIdempotent => _isIdempotent;

            public IFluentMethodConfiguration RequireIdempotent(bool enabled = true)
            {
                _isIdempotent = enabled;
                return this;
            }

            public IFluentMethodConfiguration WithVersion(int version)
            {
                _configurations.Add(builder => builder.WithVersion(version));
                return this;
            }

            public IFluentMethodConfiguration WithKeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new()
            {
                _configurations.Add(builder => builder.WithKeyGenerator<TGenerator>());
                return this;
            }

            public IFluentMethodConfiguration When(Func<CacheContext, bool> predicate)
            {
                if (predicate == null) throw new ArgumentNullException(nameof(predicate));
                _configurations.Add(builder => builder.When(predicate));
                return this;
            }

            public CacheEntryOptions BuildOptions(CacheEntryOptions? defaultOptions, CacheEntryOptions? groupOptions)
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

                if (defaultOptions?.SlidingExpiration is TimeSpan sliding)
                {
                    builder.WithSlidingExpiration(sliding);
                }

                if (defaultOptions?.RefreshAhead is TimeSpan refresh)
                {
                    builder.RefreshAhead(refresh);
                }

                if (defaultOptions?.StampedeProtection is StampedeProtectionOptions stampede)
                {
                    builder.WithStampedeProtection(stampede);
                }

                if (defaultOptions?.DistributedLock is DistributedLockOptions distributedLock)
                {
                    builder.WithDistributedLock(distributedLock.Timeout, distributedLock.MaxConcurrency);
                }

                if (defaultOptions?.Metrics is ICacheMetrics metrics)
                {
                    builder.WithMetrics(metrics);
                }

                if (defaultOptions?.Version is int version)
                {
                    builder.WithVersion(version);
                }

                if (defaultOptions?.KeyGeneratorType is Type generatorType)
                {
                    builder.WithKeyGenerator(generatorType);
                }

                if (defaultOptions?.Predicate is Func<CacheContext, bool> predicate)
                {
                    builder.WithPredicate(predicate);
                }

                if (groupOptions != null)
                {
                    if (groupOptions.Duration.HasValue)
                    {
                        builder.WithDuration(groupOptions.Duration.Value);
                    }

                    if (groupOptions.Tags.Count > 0)
                    {
                        builder.WithTags(groupOptions.Tags.ToArray());
                    }

                    if (groupOptions.SlidingExpiration is TimeSpan groupSliding)
                    {
                        builder.WithSlidingExpiration(groupSliding);
                    }

                    if (groupOptions.RefreshAhead is TimeSpan groupRefresh)
                    {
                        builder.RefreshAhead(groupRefresh);
                    }

                    if (groupOptions.StampedeProtection is StampedeProtectionOptions groupStampede)
                    {
                        builder.WithStampedeProtection(groupStampede);
                    }

                    if (groupOptions.DistributedLock is DistributedLockOptions groupLock)
                    {
                        builder.WithDistributedLock(groupLock.Timeout, groupLock.MaxConcurrency);
                    }

                    if (groupOptions.Metrics is ICacheMetrics groupMetrics)
                    {
                        builder.WithMetrics(groupMetrics);
                    }

                    if (groupOptions.Version.HasValue)
                    {
                        builder.WithVersion(groupOptions.Version.Value);
                    }

                    if (groupOptions.KeyGeneratorType != null)
                    {
                        builder.WithKeyGenerator(groupOptions.KeyGeneratorType);
                    }

                    if (groupOptions.Predicate != null)
                    {
                        builder.WithPredicate(groupOptions.Predicate);
                    }
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
