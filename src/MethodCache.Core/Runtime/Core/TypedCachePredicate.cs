namespace MethodCache.Infrastructure;

/// <summary>A typed CacheWhen predicate, DurationFor, SlidingFor or TagsFor function, wrapped to take the result as an object.</summary>
internal interface ICachePredicate
{
    Delegate Identity { get; }
    Type ResultType { get; }
    /// <summary>The setting, such as <c>CacheWhen</c>, named in errors.</summary>
    string Setting { get; }
}

internal sealed class TypedCachePredicate<T>(Func<T, bool> predicate) : ICachePredicate
{
    public Delegate Identity => predicate;
    public Type ResultType => typeof(T);
    public string Setting => "CacheWhen";
    internal bool Accept(object? value) => predicate(value is null ? default! : (T)value);
}

/// <summary>A DurationFor function, or a SlidingFor function when its setting says so.</summary>
internal sealed class TypedDuration<T>(Func<T, TimeSpan> duration, string setting = "DurationFor") : ICachePredicate
{
    public Delegate Identity => duration;
    public Type ResultType => typeof(T);
    public string Setting => setting;
    internal TimeSpan For(object? value) => duration(value is null ? default! : (T)value);
}

internal sealed class TypedTags<T>(Func<T, IEnumerable<string>?> tags) : ICachePredicate
{
    public Delegate Identity => tags;
    public Type ResultType => typeof(T);
    public string Setting => "TagsFor";

    /// <summary>
    /// The result's tags, without duplicates. Throws for a null, empty or whitespace tag, which no invalidation could
    /// reach, so the result isn't cached.
    /// </summary>
    internal string[] For(object? value)
    {
        if (tags(value is null ? default! : (T)value) is not { } given) return [];
        var list = given.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var tag in list)
        {
            if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("A cache tag cannot be null or whitespace.");
        }

        return list;
    }
}

internal static class CachePredicateCheck
{
    /// <summary>
    /// Throws when a <c>CacheWhen&lt;T&gt;</c> predicate, <c>DurationFor&lt;T&gt;</c>, <c>SlidingFor&lt;T&gt;</c> or <c>TagsFor&lt;T&gt;</c> function can never see a result of
    /// <paramref name="resultType"/>, for example a rule for <c>string</c> on a call that returns <c>int</c>, instead of
    /// silently caching nothing.
    /// </summary>
    internal static void EnsureMatches(Delegate? typedSetting, Type resultType, string methodName)
    {
        if (typedSetting?.Target is not ICachePredicate typed) return;
        var expected = typed.ResultType;
        if (expected.IsAssignableFrom(resultType) || resultType.IsAssignableFrom(expected)) return;
        throw new InvalidOperationException(
            $"{typed.Setting}<{Readable(expected)}> can't check the {Readable(resultType)} result of {methodName}, so nothing would be cached. " +
            $"Use the result type of the call, or set it for the method with Method(...).{typed.Setting}(...).");
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short",
        [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long",
        [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal",
        [typeof(char)] = "char", [typeof(string)] = "string", [typeof(object)] = "object",
    };

    /// <summary>A type as C# writes it, such as <c>int?</c> or <c>List&lt;string&gt;</c>, not <c>Nullable`1</c>.</summary>
    internal static string Readable(Type type)
    {
        if (Keywords.TryGetValue(type, out var keyword)) return keyword;
        if (Nullable.GetUnderlyingType(type) is { } underlying) return Readable(underlying) + "?";
        if (type.IsArray) return Readable(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (!type.IsGenericType) return type.Name;
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return (tick < 0 ? name : name[..tick]) + "<" + string.Join(", ", type.GetGenericArguments().Select(Readable)) + ">";
    }
}
