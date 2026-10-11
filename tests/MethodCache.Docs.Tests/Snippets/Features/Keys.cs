using MethodCache;
using MethodCache.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyApp
{
    public sealed record User(int Id, string Name);

    // begin-snippet: keys-arguments
    public sealed record UserQuery(string Name, int Page);

    public interface IUserService
    {
        [Cache] Task<User> GetAsync(int id);                     // MyApp.IUserService.GetAsync:42
        [Cache] Task<List<User>> SearchAsync(UserQuery query);   // MyApp.IUserService.SearchAsync:(alice:2)
        [Cache] Task<List<User>> ByIdsAsync(int[] ids);          // MyApp.IUserService.ByIdsAsync:3f2a… (hashed)
    }
    // end-snippet

    // begin-snippet: keys-provider
    public sealed class TenantId(Guid value) : ICacheKeyProvider
    {
        private readonly Guid _value = value;   // private state: MethodCache can't see it

        public string CacheKeyPart => _value.ToString("N");
    }
    // end-snippet

    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: keys-json-generator
            builder.Services.AddMethodCache();
            builder.Services.Replace(ServiceDescriptor.Singleton<ICacheKeyGenerator, JsonKeyGenerator>());
            // end-snippet
        }
    }
}
