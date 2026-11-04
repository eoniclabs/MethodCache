using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MethodCache.EntityFrameworkCore;

/// <summary>The tag templates of one context's model, with each placeholder resolved to a property.</summary>
internal sealed class EntityTagMap
{
    private readonly Dictionary<IReadOnlyEntityType, EntityTags> _byEntityType;

    private EntityTagMap(Dictionary<IReadOnlyEntityType, EntityTags> byEntityType)
    {
        _byEntityType = byEntityType;
        NeedsStoredValues = byEntityType.Values.Any(static tags => tags.NeedsStoredValues);
    }

    public bool IsEmpty => _byEntityType.Count == 0;

    /// <summary>
    /// A template reads a property outside the primary key, so the old tags of a row attached without loading it
    /// come from the database.
    /// </summary>
    public bool NeedsStoredValues { get; }

    public bool TryGet(IReadOnlyEntityType entityType, [MaybeNullWhen(false)] out EntityTags tags)
        => _byEntityType.TryGetValue(entityType, out tags);

    /// <summary>Resolves every template against <paramref name="model"/>, throwing for anything that doesn't fit it.</summary>
    public static EntityTagMap Build(IModel model, Type contextType, EntityFrameworkCoreOptions options)
    {
        var mapped = new Dictionary<IReadOnlyEntityType, EntityTagTemplate[]>();
        var errors = new List<string>();
        foreach (var (clrType, templates) in options.Entities)
        {
            var entityType = model.FindEntityType(clrType);
            if (entityType == null)
            {
                errors.Add($"{clrType.Name} is not an entity type of {contextType.Name}.");
                continue;
            }
            mapped[entityType] = templates.Select(template => EntityTagTemplate.Parse(template, entityType, errors)).ToArray();
        }
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"MethodCache can't use the entity tags registered for {contextType.Name}:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(static error => "- " + error)));
        }

        // A derived entity type invalidates the tags of every mapped type it derives from
        var templatesByType = new Dictionary<IReadOnlyEntityType, EntityTagTemplate[]>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var templates = new List<EntityTagTemplate>();
            for (IReadOnlyEntityType? type = entityType; type != null; type = type.BaseType)
            {
                if (mapped.TryGetValue(type, out var own)) templates.AddRange(own);
            }
            if (templates.Count > 0) templatesByType[entityType] = templates.ToArray();
        }

        var principals = FindPrincipals(model, templatesByType);
        var byEntityType = new Dictionary<IReadOnlyEntityType, EntityTags>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var templates = templatesByType.GetValueOrDefault(entityType) ?? [];
            var foreignKeys = principals.GetValueOrDefault(entityType) ?? [];
            if (templates.Length > 0 || foreignKeys.Length > 0) byEntityType[entityType] = new EntityTags(entityType, templates, foreignKeys);
        }
        return new EntityTagMap(byEntityType);
    }

    /// <summary>
    /// Owned types and many-to-many join rows have no tags of their own: a change to one changes what its owner, or
    /// each side of the join, reads. Finds the foreign keys to those principals, where a principal has tags itself or
    /// through its own owner.
    /// </summary>
    private static Dictionary<IReadOnlyEntityType, IForeignKey[]> FindPrincipals(IModel model,
        Dictionary<IReadOnlyEntityType, EntityTagTemplate[]> templatesByType)
    {
        var candidates = new Dictionary<IReadOnlyEntityType, List<IForeignKey>>();
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.FindOwnership() is { } ownership) Candidates(entityType).Add(ownership);
            foreach (var skipNavigation in entityType.GetDeclaredSkipNavigations())
            {
                Candidates(skipNavigation.JoinEntityType).Add(skipNavigation.ForeignKey);
            }
        }

        // A nested owned type reaches tags through its owner's owner: repeat until nothing is added
        var tagged = new HashSet<IReadOnlyEntityType>(templatesByType.Keys);
        var result = new Dictionary<IReadOnlyEntityType, IForeignKey[]>();
        bool changed;
        do
        {
            changed = false;
            foreach (var (dependent, foreignKeys) in candidates)
            {
                var useful = foreignKeys.Where(fk => fk.PrincipalEntityType.GetDerivedTypesInclusive().Any(tagged.Contains)).Distinct().ToArray();
                if (useful.Length == 0 || (result.TryGetValue(dependent, out var known) && known.Length == useful.Length)) continue;
                result[dependent] = useful;
                tagged.Add(dependent);
                changed = true;
            }
        } while (changed);
        return result;

        List<IForeignKey> Candidates(IReadOnlyEntityType entityType)
        {
            if (!candidates.TryGetValue(entityType, out var list)) candidates[entityType] = list = [];
            return list;
        }
    }
}

/// <summary>What a change to one entity type invalidates.</summary>
internal sealed class EntityTags
{
    public EntityTags(IReadOnlyEntityType entityType, EntityTagTemplate[] templates, IForeignKey[] principals)
    {
        Templates = templates;
        Principals = principals;
        var key = entityType.FindPrimaryKey()?.Properties ?? [];
        NeedsStoredValues = templates.SelectMany(static t => t.Properties).Any(p => !key.Contains(p));
    }

    public EntityTagTemplate[] Templates { get; }

    /// <summary>Foreign keys to an owner or to each side of a many-to-many join, whose tags a change invalidates too.</summary>
    public IForeignKey[] Principals { get; }

    /// <summary>A template reads a property outside the primary key, which a row attached without loading may not hold.</summary>
    public bool NeedsStoredValues { get; }
}

/// <summary>One tag template: literal text with placeholders resolved to entity properties.</summary>
internal sealed class EntityTagTemplate
{
    private readonly string _text;
    private readonly (int Start, int Length, IProperty Property)[] _placeholders;

    private EntityTagTemplate(string text, (int, int, IProperty)[] placeholders)
    {
        _text = text;
        _placeholders = placeholders;
        Properties = placeholders.Select(static p => p.Item3).Distinct().ToArray();
    }

    /// <summary>The properties the placeholders read.</summary>
    public IProperty[] Properties { get; }

    /// <summary>
    /// Finds the placeholders in one pass from left to right, as tag templates on cached methods do: the exact
    /// property name first, then the only case-insensitive match.
    /// </summary>
    public static EntityTagTemplate Parse(string template, IEntityType entityType, List<string> errors)
    {
        var placeholders = new List<(int, int, IProperty)>();
        var properties = entityType.GetProperties().ToArray();
        var position = 0;
        while (position < template.Length)
        {
            var open = template.IndexOf('{', position);
            var close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (close < 0) break;

            var name = template.Substring(open + 1, close - open - 1);
            var property = properties.FirstOrDefault(p => p.Name == name);
            if (property == null)
            {
                var matches = properties.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1) property = matches[0];
                else if (matches.Length > 1)
                    errors.Add($"\"{template}\" on {entityType.ClrType.Name}: {{{name}}} matches {string.Join(" and ", matches.Select(static m => m.Name))}. Write the property name exactly.");
                else
                    errors.Add($"\"{template}\" on {entityType.ClrType.Name}: {{{name}}} is not a property. Its properties are {string.Join(", ", properties.Select(static p => p.Name))}.");
            }
            if (property != null) placeholders.Add((open, close - open + 1, property));
            position = close + 1;
        }
        return new EntityTagTemplate(template, placeholders.ToArray());
    }

    /// <summary>Fills in the template with one set of a row's values, formatted as cached methods format arguments.</summary>
    public string Format<TValues>(TValues values) where TValues : IRowValues
    {
        if (_placeholders.Length == 0) return _text;
        var result = new StringBuilder(_text.Length + 16);
        var position = 0;
        foreach (var (start, length, property) in _placeholders)
        {
            result.Append(_text, position, start - position).Append(Generated.KeyParts.TagPart(values[property]));
            position = start + length;
        }
        return result.Append(_text, position, _text.Length - position).ToString();
    }
}

/// <summary>The values of one row, as they are before or after a save.</summary>
internal interface IRowValues
{
    object? this[IProperty property] { get; }
}
