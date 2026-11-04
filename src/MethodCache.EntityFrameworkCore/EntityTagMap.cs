using System.Text;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MethodCache.EntityFrameworkCore;

/// <summary>The tag templates of one context's model, with each placeholder resolved to a property.</summary>
internal sealed class EntityTagMap
{
    private readonly Dictionary<IReadOnlyEntityType, EntityTagTemplate[]> _byEntityType;

    private EntityTagMap(Dictionary<IReadOnlyEntityType, EntityTagTemplate[]> byEntityType) => _byEntityType = byEntityType;

    public bool IsEmpty => _byEntityType.Count == 0;

    public bool TryGet(IReadOnlyEntityType entityType, out EntityTagTemplate[] templates)
        => _byEntityType.TryGetValue(entityType, out templates!);

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
        var byEntityType = new Dictionary<IReadOnlyEntityType, EntityTagTemplate[]>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var templates = new List<EntityTagTemplate>();
            for (IReadOnlyEntityType? type = entityType; type != null; type = type.BaseType)
            {
                if (mapped.TryGetValue(type, out var own)) templates.AddRange(own);
            }
            if (templates.Count > 0) byEntityType[entityType] = templates.ToArray();
        }
        return new EntityTagMap(byEntityType);
    }
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
    }

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

    /// <summary>Fills in the template with one set of an entry's values, formatted as cached methods format arguments.</summary>
    public string Format(PropertyValues values)
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
