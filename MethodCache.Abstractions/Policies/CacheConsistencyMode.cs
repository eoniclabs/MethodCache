namespace MethodCache.Abstractions.Policies;

public enum CacheConsistencyMode
{
    Unspecified = 0,
    Strong = 1,
    Eventual = 2,
    BestEffort = 3
}
