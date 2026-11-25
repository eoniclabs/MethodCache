using System.Net.Http.Json;
using System.Security.Claims;
using MethodCache;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Blazor
{
    public sealed record Order(int Id, decimal Total);

    // begin-snippet: blazor-user-as-argument
    public interface IOrders
    {
        [Cache(Duration = "5m")]
        Task<IReadOnlyList<Order>> ForUserAsync(string userId);
    }

    // Not cached: reads the signed-in user of the circuit and passes the id on, so it is part of the key
    public sealed class MyOrders(AuthenticationStateProvider auth, IOrders orders)
    {
        public async Task<IReadOnlyList<Order>> GetAsync()
        {
            var state = await auth.GetAuthenticationStateAsync();
            var userId = state.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException("Nobody is signed in.");
            return await orders.ForUserAsync(userId);
        }
    }
    // end-snippet

    // begin-snippet: blazor-wasm-api-client
    public interface ICatalog
    {
        [Cache(Duration = "10m")]
        Task<IReadOnlyList<Product>> SearchAsync(string text);
    }

    // In the WebAssembly project: each search reaches the server once per browser tab within ten minutes
    public sealed class CatalogClient(HttpClient http) : ICatalog
    {
        public async Task<IReadOnlyList<Product>> SearchAsync(string text)
            => await http.GetFromJsonAsync<List<Product>>($"api/products?text={Uri.EscapeDataString(text)}") ?? [];
    }
    // end-snippet

    public sealed record Product(int Id, string Name);

    public static class Setup
    {
        public static void Register(IServiceCollection services)
        {
            // begin-snippet: blazor-wasm-registration
            // Program.cs of the WebAssembly project
            services.AddScoped<ICatalog, CatalogClient>();
            services.AddMethodCache();   // memory only: the browser can't reach Redis, SQL Server or PostgreSQL
            // end-snippet
        }

        // begin-snippet: blazor-wasm-sign-out
        // Before signing out in the browser, so the next user of this tab doesn't see the previous user's results
        public static async Task SignOutAsync(ICacheAdministrator cache)
        {
            await cache.ClearAsync();
            // ... then sign out as usual
        }
        // end-snippet
    }
}
