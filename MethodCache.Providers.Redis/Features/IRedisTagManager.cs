using System.Collections.Generic;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    public interface IRedisTagManager
    {
        Task AssociateTagsAsync(string key, IEnumerable<string> tags);
        Task<string[]> GetKeysByTagsAsync(string[] tags);
        Task RemoveTagAssociationsAsync(IEnumerable<string> keys, string[] tags);
        Task RemoveAllTagAssociationsAsync(string key);
    }
}