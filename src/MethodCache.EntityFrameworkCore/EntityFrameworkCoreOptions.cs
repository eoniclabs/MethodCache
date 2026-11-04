namespace MethodCache.EntityFrameworkCore;

/// <summary>
/// Maps entity types to the cache tags that a change to one of their rows invalidates, for
/// <c>mc.UseEntityFrameworkCore&lt;TContext&gt;(ef =&gt; ...)</c>.
/// </summary>
public sealed class EntityFrameworkCoreOptions
{
    private readonly Dictionary<Type, List<string>> _entities = new();

    internal EntityFrameworkCoreOptions()
    {
    }

    internal IReadOnlyDictionary<Type, List<string>> Entities => _entities;

    /// <summary>
    /// Invalidates <paramref name="tagTemplates"/> after a commit that added, changed or deleted a
    /// <typeparamref name="TEntity"/>.
    /// </summary>
    /// <typeparam name="TEntity">An entity type of the context, or a base type that the context maps.</typeparam>
    /// <param name="tagTemplates">
    /// Tags such as <c>"products"</c> or <c>"product:{Id}"</c>. A name in braces is a property of the entity, also a
    /// shadow property such as a foreign key, and is formatted exactly as the same value is in
    /// <c>[Cache(Tags = ...)]</c>, so <c>"product:{Id}"</c> here matches <c>"product:{id}"</c> on a cached method.
    /// For a changed row, the tags are built from both the old and the new values.
    /// </param>
    /// <returns>These options, for chaining.</returns>
    /// <exception cref="ArgumentException">No tag is given, or a tag is <see langword="null"/>, empty or whitespace.</exception>
    public EntityFrameworkCoreOptions Entity<TEntity>(params string[] tagTemplates) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(tagTemplates);
        if (tagTemplates.Length == 0) throw new ArgumentException("Give at least one tag.", nameof(tagTemplates));
        foreach (var template in tagTemplates) ArgumentException.ThrowIfNullOrWhiteSpace(template, nameof(tagTemplates));

        if (!_entities.TryGetValue(typeof(TEntity), out var templates)) _entities[typeof(TEntity)] = templates = [];
        foreach (var template in tagTemplates)
        {
            if (!templates.Contains(template, StringComparer.Ordinal)) templates.Add(template);
        }
        return this;
    }
}
