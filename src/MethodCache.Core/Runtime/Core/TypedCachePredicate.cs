namespace MethodCache.Infrastructure;

/// <summary>A typed CacheWhen predicate or DurationFor function, wrapped to take the result as an object.</summary>
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

internal sealed class TypedDuration<T>(Func<T, TimeSpan> duration) : ICachePredicate
{
    public Delegate Identity => duration;
    public Type ResultType => typeof(T);
    public string Setting => "DurationFor";
    internal TimeSpan For(object? value) => duration(value is null ? default! : (T)value);
}

internal static class CachePredicateCheck
{
    /// <summary>
    /// Throws when a <c>CacheWhen&lt;T&gt;</c> predicate or <c>DurationFor&lt;T&gt;</c> function can never see a result of
    /// <paramref name="resultType"/>, for example a rule for <c>string</c> on a call that returns <c>int</c>, instead of
    /// silently caching nothing.
    /// </summary>
    internal static void EnsureMatches(Delegate? typedSetting, Type resultType, string methodName)
    {
        if (typedSetting?.Target is not ICachePredicate typed) return;
        var expected = typed.ResultType;
        if (expected.IsAssignableFrom(resultType) || resultType.IsAssignableFrom(expected)) return;
        throw new InvalidOperationException(
            $"{typed.Setting}<{expected.Name}> can't check the {resultType.Name} result of {methodName}, so nothing would be cached. " +
            $"Use the result type of the call, or set it for the method with Method(...).{typed.Setting}(...).");
    }
}
