namespace MethodCache.Abstractions.Policies;

public enum CacheWriteStrategy
{
    Unspecified = 0,
    WriteThrough = 1,
    WriteBehind = 2
}
