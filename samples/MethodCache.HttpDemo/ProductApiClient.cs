using System.Net;
using System.Net.Http.Json;

namespace MethodCache.HttpDemo;

public sealed record HttpObservation(HttpStatusCode Status, string Body, string? CacheOutcome);

/// <summary>A normal typed HttpClient: caching is added to its registration, not to these methods.</summary>
public sealed class ProductApiClient(HttpClient http) : IDisposable
{
    public async Task<HttpObservation> GetAsync(string path)
    {
        using var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return new(response.StatusCode, await response.Content.ReadAsStringAsync(),
            response.Headers.TryGetValues("X-Cache", out var values) ? values.Single() : null);
    }

    public async Task UpdatePriceAsync(decimal price)
    {
        using var response = await http.PutAsJsonAsync("/product", new { price });
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => http.Dispose();
}
