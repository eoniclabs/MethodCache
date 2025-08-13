namespace MethodCache.Core
{
    public interface ICacheKeyProvider
    {
        string CacheKeyPart { get; }
    }
}
