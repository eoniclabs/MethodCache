using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace MethodCache.Core.PolicyPipeline.Model;

/// <summary>
/// Builds method ids in the format the source generator uses, so policies configured in code or at runtime
/// reach the generated decorators. A method is named by the interface that declares it, and generic types by their
/// definition, for example <c>MyApp.IRepository&lt;T&gt;.Get</c>, so one entry configures every closed type. An
/// overloaded method adds its parameter types, for example <c>MyApp.IUsers.Find(int)</c> and
/// <c>MyApp.IUsers.Find(string)</c>.
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

    /// <summary>How a method id is written, for error messages.</summary>
    public const string Format = "[Assembly]Namespace.IService.Method, with the parameter types of an overloaded method, as in [MyApp]MyApp.IUserService.Find(int)";

    /// <summary>
    /// The method a lambda such as <c>s =&gt; s.GetAsync(0)</c> calls, checked to return a result that can be cached.
    /// </summary>
    /// <exception cref="ArgumentException">The lambda doesn't call a method, or the method returns nothing to cache.</exception>
    public static MethodInfo CachedCall(LambdaExpression expression, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(expression, parameterName);
        var body = expression.Body;
        // A lambda typed to return object boxes a value-type result
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } conversion)
        {
            body = conversion.Operand;
        }

        if (body is not MethodCallExpression call)
        {
            throw new ArgumentException("The expression must call one method of the service, such as s => s.GetAsync(Any<int>.Value).", parameterName);
        }

        var returnType = call.Method.ReturnType;
        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            var returns = returnType == typeof(void) ? "void" : returnType.Name + " without a result";
            throw new ArgumentException(
                $"'{For(call.Method)}' returns {returns}, so it has nothing to cache. Only a method that returns a value, Task<T> or ValueTask<T> can have [Cache].",
                parameterName);
        }

        return call.Method;
    }

    /// <summary>The id of the cached method a lambda such as <c>s =&gt; s.GetAsync(0)</c> calls.</summary>
    /// <exception cref="ArgumentException">The lambda doesn't call a method, or the method returns nothing to cache.</exception>
    public static string ForCachedCall(LambdaExpression expression) => For(CachedCall(expression, "method"));

    /// <summary>Explains that <paramref name="id"/> names no cached method, suggesting the closest cached id.</summary>
    public static string NotCached(string id, IReadOnlyCollection<string> cachedMethods)
    {
        var closest = Closest(id, cachedMethods);
        var hint = closest == null ? string.Empty : $"; did you mean '{closest}'";
        return $"'{id}' is not a cached method{hint}. A method id has the form {Format}. The method needs [Cache] on its " +
               "interface (or [assembly: CacheInterface]), and its service must be registered before AddMethodCache.";
    }

    /// <summary>The id in <paramref name="candidates"/> with the fewest edits from <paramref name="id"/>, ignoring case.</summary>
    public static string? Closest(string id, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates.Order(StringComparer.Ordinal))
        {
            var distance = EditDistance(id, candidate);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    public static string For(Type serviceType, string methodName)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentException.ThrowIfNullOrEmpty(methodName);

        var builder = new StringBuilder();
        builder.Append(Internal.AssemblyIdentity.PolicyPrefix(serviceType.Assembly.GetName().Name!));
        AppendType(builder, serviceType.IsGenericType ? serviceType.GetGenericTypeDefinition() : serviceType);
        return builder.Append('.').Append(methodName).ToString();
    }

    /// <summary>The id of <paramref name="method"/>, named by the type that declares it.</summary>
    public static string For(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var declaringType = method.DeclaringType ?? throw new ArgumentException("The method has no declaring type.", nameof(method));
        var definitionType = declaringType.IsGenericType ? declaringType.GetGenericTypeDefinition() : declaringType;

        var builder = new StringBuilder();
        builder.Append(Internal.AssemblyIdentity.PolicyPrefix(definitionType.Assembly.GetName().Name!));
        AppendType(builder, definitionType);
        builder.Append('.').Append(method.Name);

        var overloads = definitionType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == method.Name && !m.IsSpecialName)
            .ToList();
        if (overloads.Count < 2)
        {
            return builder.ToString();
        }

        // The definition's parameters, so a generic type's T is named T, as the generator names it
        var definition = declaringType.IsGenericType
            ? (MethodInfo)MethodBase.GetMethodFromHandle(method.MethodHandle, definitionType.TypeHandle)!
            : method;
        if (definition.IsGenericMethod)
        {
            definition = definition.GetGenericMethodDefinition();
            builder.Append("``").Append(definition.GetGenericArguments().Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.Append('(');
        var parameters = definition.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            AppendParameterType(builder, parameters[i].ParameterType);
        }

        return builder.Append(')').ToString();
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(object)] = "object",
    };

    /// <summary>
    /// A parameter type as the generator writes it: C# keywords for built-in types, otherwise the qualified name with
    /// its type arguments, <c>?</c> for nullable value types and <c>[]</c> for arrays.
    /// </summary>
    private static void AppendParameterType(StringBuilder builder, Type type)
    {
        if (type.IsByRef)
        {
            type = type.GetElementType()!;
        }

        if (type.IsArray)
        {
            AppendParameterType(builder, type.GetElementType()!);
            builder.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
            return;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            AppendParameterType(builder, underlying);
            builder.Append('?');
            return;
        }

        if (Keywords.TryGetValue(type, out var keyword))
        {
            builder.Append(keyword);
            return;
        }

        if (type.IsGenericParameter)
        {
            builder.Append(type.Name);
            return;
        }

        var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        var inherited = type.DeclaringType?.GetGenericArguments().Length ?? 0;
        if (type.DeclaringType is { } declaringType)
        {
            if (inherited > 0)
            {
                declaringType = declaringType.GetGenericTypeDefinition().MakeGenericType(arguments.Take(inherited).ToArray());
            }

            AppendParameterType(builder, declaringType);
            builder.Append('.');
        }
        else if (!type.IsGenericParameter && !string.IsNullOrEmpty(type.Namespace))
        {
            builder.Append(type.Namespace).Append('.');
        }

        var name = type.Name;
        var tick = name.IndexOf('`');
        builder.Append(tick >= 0 ? name.Substring(0, tick) : name);
        if (arguments.Length > inherited)
        {
            builder.Append('<');
            for (var i = inherited; i < arguments.Length; i++)
            {
                if (i > inherited)
                {
                    builder.Append(", ");
                }

                AppendParameterType(builder, arguments[i]);
            }

            builder.Append('>');
        }
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
