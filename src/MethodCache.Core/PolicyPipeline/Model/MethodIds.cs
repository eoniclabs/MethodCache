using System.Text;

namespace MethodCache.Core.PolicyPipeline.Model;

/// <summary>
/// Builds method ids in the format the source generator uses, so policies configured in code or at runtime
/// reach the generated decorators. Generic types are named by their definition, for example
/// <c>MyApp.IRepository&lt;T&gt;.Get</c>, so one entry configures every closed type.
/// </summary>
internal static class MethodIds
{
    /// <summary>Policies under this id are defaults for every cached method.</summary>
    public const string AllMethods = "*";

    private const string GroupPrefix = "group:";

    /// <summary>Policies under this id are defaults for the methods in group <paramref name="name"/>.</summary>
    public static string Group(string name) => GroupPrefix + name;

    /// <summary>Whether <paramref name="id"/> holds defaults rather than one method's policy.</summary>
    public static bool IsDefaults(string id)
        => id == AllMethods || id.StartsWith(GroupPrefix, StringComparison.Ordinal);

    public static string For(Type serviceType, string methodName)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentException.ThrowIfNullOrEmpty(methodName);

        var builder = new StringBuilder();
        AppendType(builder, serviceType.IsGenericType ? serviceType.GetGenericTypeDefinition() : serviceType);
        return builder.Append('.').Append(methodName).ToString();
    }

    private static void AppendType(StringBuilder builder, Type type)
    {
        if (type.DeclaringType is { } declaringType)
        {
            AppendType(builder, declaringType);
            builder.Append('.');
        }
        else if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.Append(type.Namespace).Append('.');
        }

        var name = type.Name;
        var tick = name.IndexOf('`');
        builder.Append(tick >= 0 ? name.Substring(0, tick) : name);

        if (!type.IsGenericType)
        {
            return;
        }

        // A nested type's definition repeats its declaring types' parameters first
        var inherited = type.DeclaringType?.GetGenericArguments().Length ?? 0;
        var own = type.GetGenericArguments().Skip(inherited).ToArray();
        if (own.Length > 0)
        {
            builder.Append('<').Append(string.Join(", ", own.Select(static t => t.Name))).Append('>');
        }
    }
}
