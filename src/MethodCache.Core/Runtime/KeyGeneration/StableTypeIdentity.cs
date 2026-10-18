using System.Runtime.CompilerServices;

namespace MethodCache.Infrastructure;

internal static class StableTypeIdentity
{
    private static readonly ConditionalWeakTable<Type, Identity> Names = new();
    private sealed class Identity(string text) { internal string Text { get; } = text; }
    internal static string For(Type type) => Names.GetValue(type, static item => new Identity(Create(item))).Text;

    private static string Create(Type type)
    {
        if (type.IsGenericParameter) return type.Name;
        if (type.HasElementType)
        {
            var element = For(type.GetElementType()!);
            return type.IsArray ? element + "[" + new string(',', type.GetArrayRank() - 1) + "]"
                : element + (type.IsByRef ? "&" : "*");
        }
        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        var name = Internal.AssemblyIdentity.Prefix(definition.Assembly.GetName().Name!) + (definition.FullName ?? definition.Name);
        return type.IsConstructedGenericType ? name + "[" + string.Join(",", type.GetGenericArguments().Select(For)) + "]" : name;
    }
}
