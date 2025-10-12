
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration.Surfaces.Attributes;

namespace MethodCache.SampleApp
{
    public interface ISampleService
    {
        [Cache]
        Task<string> GetDataAsync(string key);

        [CacheInvalidate(Tags = new[] { "GetDataAsync" })]
        Task InvalidateDataAsync(string key);

        [Cache("Users")]
        Task<string> GetUserDataAsync(int userId);

        [CacheInvalidate(Tags = new[] { "Users" })]
        Task InvalidateUserCacheAsync(int userId);
    }
}
