namespace MethodCache.EntityFrameworkCore;

/// <summary>
/// Maps entity types to the cache tags that a change to one of their rows invalidates, for
/// <c>mc.UseEntityFrameworkCore&lt;TContext&gt;(ef =&gt; ...)</c>.
/// </summary>
public sealed class EntityFrameworkCoreOptions
{
    private readonly Dictionary<Type, EntityTagTemplates> _entities = new();

    internal EntityFrameworkCoreOptions()
    {
    }

    internal IReadOnlyDictionary<Type, EntityTagTemplates> Entities => _entities;

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
        TemplateList.Validate(tagTemplates, nameof(tagTemplates));
        Templates(typeof(TEntity)).Changed.AddTemplates(tagTemplates, nameof(tagTemplates));
        return this;
    }

    /// <summary>
    /// Maps <typeparamref name="TEntity"/> with tags for any change and tags for a deleted row only, such as
    /// <c>ef.Entity&lt;User&gt;(user =&gt; user.Tags("user:{Id}").WhenDeleted("publications"))</c>.
    /// </summary>
    /// <typeparam name="TEntity">An entity type of the context, or a base type that the context maps.</typeparam>
    /// <param name="configure">Adds the tags with <see cref="EntityTagsBuilder{TEntity}.Tags"/> and
    /// <see cref="EntityTagsBuilder{TEntity}.WhenDeleted"/>.</param>
    /// <returns>These options, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="configure"/> adds no tag.</exception>
    public EntityFrameworkCoreOptions Entity<TEntity>(Action<EntityTagsBuilder<TEntity>> configure) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var templates = Templates(typeof(TEntity));
        var before = templates.Count;
        configure(new EntityTagsBuilder<TEntity>(templates));
        if (templates.Count == before)
        {
            if (before == 0) _entities.Remove(typeof(TEntity));
            throw new ArgumentException("Add at least one tag with Tags(...) or WhenDeleted(...).", nameof(configure));
        }
        return this;
    }

    private EntityTagTemplates Templates(Type entityType)
    {
        if (!_entities.TryGetValue(entityType, out var templates)) _entities[entityType] = templates = new EntityTagTemplates();
        return templates;
    }
}

/// <summary>
/// The tags of one entity type, for <see cref="EntityFrameworkCoreOptions.Entity{TEntity}(Action{EntityTagsBuilder{TEntity}})"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class EntityTagsBuilder<TEntity> where TEntity : class
{
    private readonly EntityTagTemplates _templates;

    internal EntityTagsBuilder(EntityTagTemplates templates) => _templates = templates;

    /// <summary>
    /// Invalidates <paramref name="tagTemplates"/> after a commit that added, changed or deleted a
    /// <typeparamref name="TEntity"/>, the same as <see cref="EntityFrameworkCoreOptions.Entity{TEntity}(string[])"/>.
    /// </summary>
    /// <param name="tagTemplates">
    /// Tags such as <c>"user:{Id}"</c>. A name in braces is a property of the entity, formatted as in
    /// <c>[Cache(Tags = ...)]</c>. For a changed row, the tags are built from both the old and the new values.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">No tag is given, or a tag is <see langword="null"/>, empty or whitespace.</exception>
    public EntityTagsBuilder<TEntity> Tags(params string[] tagTemplates)
    {
        _templates.Changed.AddTemplates(tagTemplates, nameof(tagTemplates));
        return this;
    }

    /// <summary>
    /// Invalidates <paramref name="tagTemplates"/> only after a commit that deleted a <typeparamref name="TEntity"/>.
    /// Use it for rows that the database deletes with the entity, through <c>ON DELETE CASCADE</c> or a trigger, when
    /// the entity itself changes too often to invalidate them on every save.
    /// </summary>
    /// <param name="tagTemplates">
    /// Tags such as <c>"publications"</c> or <c>"author:{Id}"</c>. A name in braces is a property of the entity,
    /// filled in from the deleted row.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">No tag is given, or a tag is <see langword="null"/>, empty or whitespace.</exception>
    public EntityTagsBuilder<TEntity> WhenDeleted(params string[] tagTemplates)
    {
        _templates.Deleted.AddTemplates(tagTemplates, nameof(tagTemplates));
        return this;
    }
}

/// <summary>The tag templates registered for one entity type.</summary>
internal sealed class EntityTagTemplates
{
    /// <summary>Invalidated when a row is added, changed or deleted.</summary>
    public TemplateList Changed { get; } = new();

    /// <summary>Invalidated when a row is deleted.</summary>
    public TemplateList Deleted { get; } = new();

    public int Count => Changed.Count + Deleted.Count;
}

/// <summary>Distinct templates in the order they were added.</summary>
internal sealed class TemplateList : List<string>
{
    public static void Validate(string[] templates, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(templates, parameterName);
        if (templates.Length == 0) throw new ArgumentException("Give at least one tag.", parameterName);
        foreach (var template in templates) ArgumentException.ThrowIfNullOrWhiteSpace(template, parameterName);
    }

    public void AddTemplates(string[] templates, string parameterName)
    {
        Validate(templates, parameterName);
        foreach (var template in templates)
        {
            if (!this.Contains(template, StringComparer.Ordinal)) Add(template);
        }
    }
}
