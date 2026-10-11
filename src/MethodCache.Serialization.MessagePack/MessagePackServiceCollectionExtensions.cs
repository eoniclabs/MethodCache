using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MethodCache.Serialization.MessagePack;

/// <summary>
/// Registers MessagePack serialization for MethodCache.
/// </summary>
public static class MessagePackServiceCollectionExtensions
{
    /// <summary>
    /// Stores cached values in the distributed cache (Redis, SQL Server or any <c>IDistributedCache</c>) as
    /// MessagePack instead of JSON. Call it together with <c>AddMethodCacheRedis()</c>,
    /// <c>AddMethodCacheSqlServer()</c> or <c>AddMethodCacheDistributedCache()</c>, in any order.
    /// </summary>
    /// <remarks>
    /// Entries written in another format are read as misses and replaced, so switching needs no cache flush.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddMethodCache();
    /// builder.Services.AddMethodCacheRedis(options => options.Configuration = "localhost:6379");
    /// builder.Services.AddMethodCacheMessagePack();
    /// </code>
    /// </example>
    public static IServiceCollection AddMethodCacheMessagePack(this IServiceCollection services, global::MessagePack.MessagePackSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<ISerializer>(new MessagePackCacheSerializer(options)));
        return services;
    }
}
