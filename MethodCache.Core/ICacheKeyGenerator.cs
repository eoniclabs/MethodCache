using MethodCache.Core.Configuration;

namespace MethodCache.Core
{
    public interface ICacheKeyGenerator
    {
        string GenerateKey(string methodName, object[] args, CacheMethodSettings settings);
    }
}
