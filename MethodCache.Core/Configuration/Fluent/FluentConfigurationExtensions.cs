using System;

namespace MethodCache.Core.Configuration.Fluent
{
    public static class FluentConfigurationExtensions
    {
        public static void ApplyFluent(this IMethodCacheConfiguration configuration, Action<IFluentMethodCacheConfiguration> configure)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var fluentConfiguration = new FluentMethodCacheConfiguration();
            configure(fluentConfiguration);
            fluentConfiguration.Apply(configuration);
        }
    }
}
