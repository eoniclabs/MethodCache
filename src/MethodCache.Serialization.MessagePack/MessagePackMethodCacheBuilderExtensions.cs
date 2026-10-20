using MethodCache.Extensibility;
using MethodCache.Serialization.MessagePack;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers MessagePack serialization for MethodCache.
/// </summary>
public static class MessagePackMethodCacheBuilderExtensions
{
    /// <summary>
    /// Stores cached values in the distributed cache (Redis or SQL Server) as MessagePack instead of JSON, with the
    /// package's defaults. The order relative to <c>UseRedis</c> or <c>UseSqlServer</c> does not matter.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddMethodCache(mc =&gt; ...)</c>.</param>
    /// <returns>The builder, for chaining.</returns>
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
    /// Stores cached values in the distributed cache as MessagePack, with serializer options changed from the
    /// package's defaults.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddMethodCache(mc =&gt; ...)</c>.</param>
    /// <param name="configure">
    /// Receives the defaults (the contractless resolver with native <see cref="DateTime"/>, LZ4 compression and
    /// <see cref="global::MessagePack.MessagePackSecurity.UntrustedData"/>) and returns the options to use. Change them
    /// with the <c>With...</c> methods, so the settings you don't change keep their defaults. Stored entries are read
    /// as untrusted data unless you replace the security setting.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddMethodCache(mc => mc
    ///     .UseRedis("localhost:6379")
    ///     .UseMessagePack(options => options.WithCompression(MessagePackCompression.None)));
    /// </code>
    /// </example>
    public static MethodCache.MethodCacheBuilder UseMessagePack(this MethodCache.MethodCacheBuilder builder,
        Func<global::MessagePack.MessagePackSerializerOptions, global::MessagePack.MessagePackSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = configure(MessagePackCacheSerializer.DefaultOptions)
            ?? throw new InvalidOperationException("The UseMessagePack callback returned null. Return the options it receives, changed with their With... methods.");
        AddMessagePack(builder.Services, options);
        return builder;
    }

    private static void AddMessagePack(IServiceCollection services, global::MessagePack.MessagePackSerializerOptions? options)
        => services.Replace(ServiceDescriptor.KeyedSingleton<ISerializer>(MethodCache.Infrastructure.OwnedServices.Key, new MessagePackCacheSerializer(options)));
}
