using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace MethodCache.Infrastructure;

/// <summary>Public argument state added by an application's collection type, beyond its elements.</summary>
internal static class KeyCollectionState
{
    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();

    internal sealed record Member(string Name, Type Type, Func<object, object?> Read);

    public static Member[] For(Type type) => Members.GetOrAdd(type, static collection =>
    {
        if (collection == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(collection)) return [];
        var members = new Dictionary<string, Member>(StringComparer.Ordinal);
        for (var current = collection; current != null && !IsFramework(current); current = current.BaseType)
        {
            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0 && !Ignored(property))
                    members.TryAdd(property.Name, new Member(property.Name, property.PropertyType, property.GetValue));
            }
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (!Ignored(field)) members.TryAdd(field.Name, new Member(field.Name, field.FieldType, field.GetValue));
            }
        }
        return members.Values.OrderBy(static member => member.Name, StringComparer.Ordinal).ToArray();
    });

    private static bool Ignored(MemberInfo member) => member.GetCustomAttributesData().Any(static attribute =>
        attribute.AttributeType.Name is "JsonIgnoreAttribute" or "IgnoreDataMemberAttribute" or "IgnoreMemberAttribute");

    private static bool IsFramework(Type type)
    {
        var assembly = type.Assembly.GetName().Name;
        return assembly is "mscorlib" or "netstandard" or "System" or "System.Private.CoreLib" ||
            assembly?.StartsWith("System.", StringComparison.Ordinal) == true ||
            assembly?.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) == true ||
            assembly?.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) == true ||
            assembly?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true;
    }
}
