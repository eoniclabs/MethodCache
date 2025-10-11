using System;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Options;

namespace MethodCache.Core.Configuration.Fluent;

internal static class CachePolicyBuilderFactory
{
    public static CachePolicyBuilder FromOptions(CacheEntryOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var builder = new CachePolicyBuilder();

        if (options.Duration.HasValue)
        {
            builder.WithDuration(options.Duration.Value);
        }

        if (options.Tags.Count > 0)
        {
            builder.SetTags(options.Tags);
        }

        if (options.KeyGeneratorType != null)
        {
            builder.WithKeyGenerator(options.KeyGeneratorType);
        }

        if (options.Version.HasValue)
        {
            builder.WithVersion(options.Version.Value);
        }

        return builder;
    }
}
