using System.Text.Json.Serialization.Metadata;

namespace MethodCache.Infrastructure;

/// <summary>
/// The types MethodCache's generator found deriving from or implementing a type that a cached result declares, such
/// as a <c>Dog</c> for a <c>List&lt;Animal&gt;</c>. The distributed cache's JSON writes each with a <c>$type</c> name
/// and restores it as itself, without changing how the application's own JSON writes these types.
/// </summary>
internal sealed class DerivedTypeRegistry
{
    private readonly Dictionary<Type, HashSet<Type>> _derived = [];

    internal int Count => _derived.Count;

    internal void Add(IReadOnlyDictionary<Type, HashSet<Type>> derived)
    {
        foreach (var (declared, types) in derived)
        {
            if (types.Count == 0) continue;
            if (!_derived.TryGetValue(declared, out var set)) _derived.Add(declared, set = []);
            set.UnionWith(types);
        }
    }

    /// <summary>Each declared type and its derived types, ordered by full name, the name a serializer stores for each.</summary>
    internal Dictionary<Type, Type[]> Snapshot()
        => _derived.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.OrderBy(static type => type.FullName, StringComparer.Ordinal).ToArray());

    /// <summary>The name a serializer stores for a derived type: its full name, unique in the application.</summary>
    internal static string NameOf(Type type) => type.FullName ?? type.Name;

    /// <summary>
    /// Adds the derived types to the contract of each declared type that has no polymorphism of its own, such as
    /// <c>[JsonDerivedType]</c>, which then wins. A derived type that isn't named, such as a proxy made at run time, is
    /// written as the nearest named type, so the check before sharing keeps it in memory.
    /// </summary>
    internal static Action<JsonTypeInfo> Modifier(IReadOnlyDictionary<Type, Type[]> derived)
        => info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object || info.PolymorphismOptions != null || !derived.TryGetValue(info.Type, out var types)) return;
            var polymorphism = new JsonPolymorphismOptions { UnknownDerivedTypeHandling = System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor };
            foreach (var type in types) polymorphism.DerivedTypes.Add(new JsonDerivedType(type, NameOf(type)));
            info.PolymorphismOptions = polymorphism;
        };

    /// <summary>A type's name as the startup log writes it: its full name, with a dot before a nested type.</summary>
    internal static string Display(Type type) => (type.FullName ?? type.Name).Replace('+', '.');
}

/// <summary>
/// A serializer of MethodCache's that writes the registered derived types with their name, so the startup log can say
/// which it restores as themselves and which it can't, such as one missing from a <c>JsonSerializerContext</c>.
/// </summary>
internal interface IRestoresDerivedTypes
{
    /// <summary>Each declared type with the derived types the serializer restores as themselves.</summary>
    IReadOnlyDictionary<Type, Type[]> Restored { get; }

    /// <summary>The registered derived types it can't write, with the declared type each was registered for.</summary>
    IReadOnlyList<(Type Declared, Type Derived)> Missing { get; }
}
