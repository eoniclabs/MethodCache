namespace MethodCache.Core.Runtime
{
    public interface ICacheKeyProvider
    {
        string CacheKeyPart { get; }
    }
}
