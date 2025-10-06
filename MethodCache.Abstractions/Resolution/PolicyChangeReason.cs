namespace MethodCache.Abstractions.Resolution;

public enum PolicyChangeReason
{
    InitialLoad = 0,
    Added = 1,
    Updated = 2,
    Removed = 3,
    Reloaded = 4
}
