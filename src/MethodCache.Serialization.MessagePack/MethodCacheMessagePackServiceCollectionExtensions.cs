using MethodCache.Extensibility;
using MethodCache.Serialization.MessagePack;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers MessagePack serialization for MethodCache.
/// </summary>
public static class MethodCacheMessagePackServiceCollectionExtensions
{
    /// <summary>
    /// Stores cached values in the distributed cache (Redis or SQL Server) as MessagePack instead of JSON, with the
    /// package's defaults. The order relative to <c>UseRedis</c> or <c>UseSqlServer</c> does not matter.
    /// </summary>
    /// <remarks>
    /// Entries written in another format are read as misses and replaced, so switching needs no cache flush.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddMethodCache(mc => mc.UseRedis("localhost:6379").UseMessagePack());
    /// </code>
    /// </example>
    public static MethodCache.MethodCacheBuilder UseMessagePack(this MethodCache.MethodCacheBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddMessagePack(builder.Services, null);
        return builder;
    }

    /// <summary>
    /// Stores cached values in the distributed cache as MessagePack with explicit serializer options.
    /// </summary>
    public static MethodCache.MethodCacheBuilder UseMessagePack(this MethodCache.MethodCacheBuilder builder, global::MessagePack.MessagePackSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        AddMessagePack(builder.Services, options);
        return builder;
    }

    private static void AddMessagePack(IServiceCollection services, global::MessagePack.MessagePackSerializerOptions? options)
        => services.Replace(ServiceDescriptor.KeyedSingleton<ISerializer>(MethodCache.Infrastructure.OwnedServices.Key, new MessagePackCacheSerializer(options)));
}
