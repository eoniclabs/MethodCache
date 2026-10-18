using MethodCache.Infrastructure;
using System.Linq.Expressions;
using System.Reflection;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Fluent
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

        public IFluentMethodCacheConfiguration FailSafe(TimeSpan duration, TimeSpan? timeout = null, TimeSpan? retryAfter = null)
        {
            // Validate now, so a bad value fails at the call that set it
            new CacheEntryOptions.Builder().FailSafe(duration, timeout, retryAfter);
            return DefaultPolicy(options => options.FailSafe(duration, timeout, retryAfter));
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

        /// <summary>
        /// Builds one draft per configured method, plus the defaults for all methods and for each group. Defaults are
        /// kept apart so they also reach methods configured only with <c>[Cache]</c>, whose own values win.
        /// </summary>
        internal IReadOnlyList<PolicyDraft> BuildMethodPolicies()
        {
            var drafts = new List<PolicyDraft>();

            if (BuildOptions(_defaultPolicyBuilders) is { } defaultOptions)
            {
                drafts.Add(CachePolicyBuilderFactory.ForDefaults("DefaultPolicy()", defaultOptions).Build(MethodIds.AllMethods));
            }

            foreach (var (groupName, groupConfiguration) in _groups)
            {
                var groupOptions = groupConfiguration.BuildOptions();
                drafts.Add(CachePolicyBuilderFactory.ForDefaults($"group '{groupName}'", groupOptions).Build(MethodIds.Group(groupName)));
            }

            foreach (var serviceConfiguration in _services.Values)
            {
                foreach (var (methodKey, methodConfiguration) in serviceConfiguration.Methods)
                {
                    var policyBuilder = CachePolicyBuilderFactory.ForMethod(methodKey, methodConfiguration.BuildOptions());

                    if (!string.IsNullOrWhiteSpace(methodConfiguration.GroupName))
                    {
                        policyBuilder.AddMetadata("group", methodConfiguration.GroupName);
                    }

                    if (methodConfiguration.CacheWhen != null)
                    {
                        policyBuilder.WithCacheWhen(methodConfiguration.CacheWhen);
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

                var methodKey = MethodIds.For(methodInfo);
                if (!_methods.TryGetValue(methodKey, out var configuration))
                {
                    configuration = new FluentMethodConfiguration(methodKey);
                    _methods.Add(methodKey, configuration);
                }

                return configuration;
            }

            public IReadOnlyDictionary<string, FluentMethodConfiguration> Methods => _methods;
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

            public IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, TResult>> method)
            {
                return new FluentMethodConfiguration<TResult>(CreateMethodConfiguration(method));
            }

            public IFluentMethodConfiguration Method(Expression<Func<TService, Task>> method)
            {
                return CreateMethodConfiguration(method);
            }

            public IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, Task<TResult>>> method)
            {
                return new FluentMethodConfiguration<TResult>(CreateMethodConfiguration(method));
            }

            public IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, ValueTask<TResult>>> method)
            {
                return new FluentMethodConfiguration<TResult>(CreateMethodConfiguration(method));
            }

            private FluentMethodConfiguration CreateMethodConfiguration(LambdaExpression expression)
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

            public Func<object?, bool>? CacheWhen { get; set; }

            public IFluentMethodConfiguration WithVersion(int version)
            {
                _configurations.Add(builder => builder.WithVersion(version));
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

        /// <summary>Typed view of a method's configuration, so <c>CacheWhen</c> gets the result type without a cast.</summary>
        private sealed class FluentMethodConfiguration<TResult> : IFluentMethodConfiguration<TResult>
        {
            private readonly FluentMethodConfiguration _inner;

            public FluentMethodConfiguration(FluentMethodConfiguration inner)
            {
                _inner = inner;
            }

            public IFluentMethodConfiguration<TResult> Configure(Action<CacheEntryOptions.Builder> configure)
            {
                _inner.Configure(configure);
                return this;
            }

            public IFluentMethodConfiguration<TResult> WithGroup(string groupName)
            {
                _inner.WithGroup(groupName);
                return this;
            }

            public IFluentMethodConfiguration<TResult> WithVersion(int version)
            {
                _inner.WithVersion(version);
                return this;
            }

            public IFluentMethodConfiguration<TResult> CacheWhen(Func<TResult, bool> predicate)
            {
                ArgumentNullException.ThrowIfNull(predicate);

                // Results reach the cache as object; a null can only be a TResult that allows null.
                _inner.CacheWhen = new TypedCachePredicate<TResult>(predicate).Accept;
                return this;
            }

            IFluentMethodConfiguration IFluentMethodConfiguration.Configure(Action<CacheEntryOptions.Builder> configure) => Configure(configure);

            IFluentMethodConfiguration IFluentMethodConfiguration.WithGroup(string groupName) => WithGroup(groupName);

            IFluentMethodConfiguration IFluentMethodConfiguration.WithVersion(int version) => WithVersion(version);
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
