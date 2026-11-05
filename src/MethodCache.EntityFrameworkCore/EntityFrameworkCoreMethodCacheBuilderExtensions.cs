using MethodCache;
using MethodCache.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Connects an Entity Framework Core context to MethodCache.
/// </summary>
public static class EntityFrameworkCoreMethodCacheBuilderExtensions
{
    /// <summary>
    /// Invalidates cache tags when <typeparamref name="TContext"/> commits: the tags of the entities mapped in
    /// <paramref name="configure"/>, and every tag queued with
    /// <see cref="MethodCacheDbContextExtensions.InvalidateAfterCommit(DbContext, string[])"/>.
    /// </summary>
    /// <typeparam name="TContext">The context, registered with <c>AddDbContext</c>, <c>AddDbContextPool</c> or
    /// <c>AddDbContextFactory</c>, before or after this call.</typeparam>
    /// <param name="builder">The builder passed to <c>AddMethodCache(mc =&gt; ...)</c>.</param>
    /// <param name="configure">Maps entity types to tags, such as <c>ef =&gt; ef.Entity&lt;Product&gt;("product:{Id}")</c>.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Without a transaction, the tags are invalidated after <c>SaveChanges</c> succeeds; inside one, after it commits,
    /// and not at all when it rolls back. A tag that doesn't fit the model, such as a placeholder that is not a
    /// property, stops the host from starting.
    /// </remarks>
    public static MethodCacheBuilder UseEntityFrameworkCore<TContext>(this MethodCacheBuilder builder,
        Action<EntityFrameworkCoreOptions>? configure = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        var options = services.FirstOrDefault(static d => d.ServiceType == typeof(EntityFrameworkCoreRegistration<TContext>))
            ?.ImplementationInstance as EntityFrameworkCoreRegistration<TContext>;
        if (options == null)
        {
            options = new EntityFrameworkCoreRegistration<TContext>();
            services.AddSingleton(options);
            services.TryAddSingleton(static sp => new EntityInvalidation<TContext>(
                sp.GetRequiredService<EntityFrameworkCoreRegistration<TContext>>().Options,
                sp.GetRequiredService<ICacheManager>(),
                sp.GetRequiredService<Logging.ILogger<EntityInvalidation<TContext>>>()));
            services.ConfigureDbContext<TContext>(static (sp, db) =>
            {
                db.AddInterceptors(sp.GetRequiredService<EntityInvalidation<TContext>>().Interceptor);
                ((IDbContextOptionsBuilderInfrastructure)db).AddOrUpdateExtension(MethodCacheOptionsExtension.Instance);
            });
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EntityTagValidator<TContext>>());
            services.TryAddSingleton(static sp => new EntityTagMapping<TContext>(
                sp.GetRequiredService<EntityFrameworkCoreRegistration<TContext>>().Options));
        }
        configure?.Invoke(options.Options);
        return builder;
    }
}

internal sealed class EntityFrameworkCoreRegistration<TContext> where TContext : DbContext
{
    public EntityFrameworkCoreOptions Options { get; } = new();
}
