using System.Net;
using System.Net.Http.Json;

namespace MethodCache.Demo;

public sealed record HttpObservation(HttpStatusCode Status, string Body, string? CacheOutcome);

/// <summary>A normal typed HttpClient: caching is added to its registration, not to these methods.</summary>
public sealed class ProductApiClient(HttpClient http)
{
    public async Task<HttpObservation> GetAsync(string path)
    {
        using var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return new(response.StatusCode, await response.Content.ReadAsStringAsync(), CacheStatus(response));
    }

    // AddDiagnosticHeaders appends the cache's own Cache-Status member (RFC 9211) after any the origin's caches added
    private static string? CacheStatus(HttpResponseMessage response)
        => response.Headers.TryGetValues("Cache-Status", out var values)
            ? values.LastOrDefault(value => value.StartsWith("MethodCache;", StringComparison.Ordinal))
            : null;

    public async Task UpdatePriceAsync(decimal price)
    {
        using var response = await http.PutAsJsonAsync("/product", new { price });
        response.EnsureSuccessStatusCode();
    }
}
