using System.Threading.Tasks;
using MethodCache.Core.Configuration;

namespace MethodCache.Core
{
    public interface ICacheManager
    {
        Task<T> GetOrCreateAsync<T>(string methodName, object[] args, System.Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent);
        Task InvalidateByTagsAsync(params string[] tags);
    }
}
