namespace MethodCache.Infrastructure;

internal interface ICachePredicate
{
    Delegate Identity { get; }
    Type ResultType { get; }
}

internal sealed class TypedCachePredicate<T>(Func<T, bool> predicate) : ICachePredicate
{
    public Delegate Identity => predicate;
    public Type ResultType => typeof(T);
    internal bool Accept(object? value) => predicate(value is null ? default! : (T)value);
}

internal static class CachePredicateCheck
{
    /// <summary>
    /// Throws when a <c>CacheWhen&lt;T&gt;</c> predicate can never see a result of <paramref name="resultType"/>, for
    /// example a rule for <c>string</c> on a call that returns <c>int</c>, instead of silently caching nothing.
    /// </summary>
    internal static void EnsureMatches(Func<object?, bool>? cacheWhen, Type resultType, string methodName)
    {
        if (cacheWhen?.Target is not ICachePredicate typed) return;
        var expected = typed.ResultType;
        if (expected.IsAssignableFrom(resultType) || resultType.IsAssignableFrom(expected)) return;
        throw new InvalidOperationException(
            $"CacheWhen<{expected.Name}> can't check the {resultType.Name} result of {methodName}, so nothing would be cached. " +
            "Use the result type of the call, or set the predicate for the method with Method(...).CacheWhen(...).");
    }
}
