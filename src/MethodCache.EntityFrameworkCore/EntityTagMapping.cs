using MethodCache.Core.Management;
using Microsoft.EntityFrameworkCore;

namespace MethodCache.EntityFrameworkCore;

/// <summary>
/// The entity types and tags that <c>mc.UseEntityFrameworkCore&lt;TContext&gt;(ef =&gt; ef.Entity&lt;T&gt;(...))</c>
/// mapped, for tests and tools that check them. Resolve it from the service provider.
/// </summary>
/// <typeparam name="TContext">The context the entity types were mapped for.</typeparam>
/// <remarks><c>TagCoverage</c> in MethodCache.Testing counts these tags as invalidated by itself.</remarks>
/// <example>
/// <code>
/// var mapping = provider.GetRequiredService&lt;EntityTagMapping&lt;ShopDb&gt;&gt;();
/// Assert.Contains(mapping.Entities, entity =&gt; entity.EntityType == typeof(Product));
/// </code>
/// </example>
public sealed class EntityTagMapping<TContext> : IInvalidatedTagSource where TContext : DbContext
{
    internal EntityTagMapping(EntityFrameworkCoreOptions options)
    {
        Entities = options.Entities
            .Select(static entity => new MappedEntity(entity.Key, entity.Value.Changed.ToArray(), entity.Value.Deleted.ToArray()))
            .ToArray();
        AllTags = Entities.SelectMany(static entity => entity.Tags.Concat(entity.WhenDeleted)).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Each mapped entity type with its tags, in the order they were mapped.</summary>
    public IReadOnlyList<MappedEntity> Entities { get; }

    /// <summary>Every tag template of the mapping once, both for any change and for a deleted row, such as <c>"product:{Id}"</c>.</summary>
    public IReadOnlyList<string> AllTags { get; }

    IReadOnlyList<string> IInvalidatedTagSource.InvalidatedTags => AllTags;
}

/// <summary>An entity type that <see cref="EntityFrameworkCoreOptions.Entity{TEntity}(string[])"/> mapped, and its tags.</summary>
/// <param name="EntityType">The entity type, or the base type that was mapped.</param>
/// <param name="Tags">The tag templates a commit that adds, changes or deletes a row invalidates.</param>
/// <param name="WhenDeleted">The tag templates only a commit that deletes a row invalidates.</param>
public sealed record MappedEntity(Type EntityType, IReadOnlyList<string> Tags, IReadOnlyList<string> WhenDeleted);
