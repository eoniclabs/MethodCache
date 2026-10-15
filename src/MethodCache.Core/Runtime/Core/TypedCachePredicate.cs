namespace MethodCache.Infrastructure;

internal interface ICachePredicate { Delegate Identity { get; } }
internal sealed class TypedCachePredicate<T>(Func<T, bool> predicate) : ICachePredicate
{
    public Delegate Identity => predicate;
    internal bool Accept(object? value) => predicate(value is null ? default! : (T)value);
}
