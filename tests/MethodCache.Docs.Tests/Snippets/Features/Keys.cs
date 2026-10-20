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
        [Cache] Task<User> GetAsync(int id);                     // [MyApp]MyApp.IUserService.GetAsync:42
        [Cache] Task<List<User>> SearchAsync(UserQuery query);   // [MyApp]MyApp.IUserService.SearchAsync:(alice:2)
        [Cache] Task<List<User>> ByIdsAsync(int[] ids);          // [MyApp]MyApp.IUserService.ByIdsAsync:[3:1:2:3]
        [Cache] Task<int> CountAsync(Dictionary<string, int[]> filters); // [MyApp]MyApp.IUserService.CountAsync:{hash} (hashed)
    }
    // end-snippet

    public sealed record Invoice(int Id);

    public interface IInvoiceService
    {
        [Cache(Version = 2)] Task<Invoice> GetAsync(int id);
        [Cache] Task<Invoice> FindAsync(int id);
        [Cache] Task<Invoice> FindAsync(string number);
    }

    // begin-snippet: keys-scoped
    public interface IDocumentStore
    {
        // The document path is an argument; the interface and method remain part of the key.
        [Cache] Task<byte[]> GetAsync(string key);
    }
    // end-snippet

    public static class KeyExamples
    {
        public static (string Versioned, string Overload) Build(IMethodCacheKeys keys)
        {
            // begin-snippet: keys-versioned-and-overloaded
            // A method with a Version: "[MyApp]MyApp.IInvoiceService.GetAsync:v2:42"
            var versioned = keys.For<IInvoiceService>(invoices => invoices.GetAsync(42));

            // An overloaded method: "[MyApp]MyApp.IInvoiceService.FindAsync(string):INV-7"
            var overload = keys.For<IInvoiceService>(invoices => invoices.FindAsync("INV-7"));
            // end-snippet
            return (versioned, overload);
        }

        public static IReadOnlyList<CacheEntryInfo> ListMethod(IMethodCacheKeys keys, ICacheAdministrator admin)
        {
            // begin-snippet: keys-method-id
            // "[MyApp]MyApp.IInvoiceService.FindAsync(string)": the arguments don't matter
            var methodId = keys.MethodId<IInvoiceService>(invoices => invoices.FindAsync(""));
            var entries = admin.ListEntries(methodId: methodId);
            // end-snippet
            return entries;
        }

        public static async Task<InvalidationResult> InvalidateMethod(IMethodCacheKeys keys, ICacheAdministrator admin)
        {
            // begin-snippet: keys-invalidate-method
            var methodId = keys.MethodId<IInvoiceService>(invoices => invoices.FindAsync(""));
            var result = await admin.InvalidateMethodAsync(methodId);   // every result of the method, on every instance
            // end-snippet
            return result;
        }
    }

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
            builder.Services.AddMethodCache(cache => cache.UseJson());
            // end-snippet
        }
    }
}
