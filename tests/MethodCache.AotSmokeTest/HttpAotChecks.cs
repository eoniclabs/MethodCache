using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MethodCache;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;

internal static class HttpAotChecks
{
    internal static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddSingleton<ICacheManager, NoOpCacheManager>();
        var origin = new Origin();
        services.AddHttpClient("aot-http").AddHttpCaching(options => options.AllowUnverifiedTransport = true)
            .ConfigurePrimaryHttpMessageHandler(() => origin);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("aot-http");
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var callerA = "aot-caller-a";
        var callerB = "aot-caller-b-" + new string('b', 600);
        Check(await ReadAsync(callerA) == "origin-1" && await ReadAsync(callerA) == "origin-1", "HTTP AOT hit failed.");
        Check(await ReadAsync(callerB) == "origin-2" && await ReadAsync(callerB) == "origin-2", "HTTP AOT variant isolation failed.");
        Check(origin.Calls == 2, "HTTP AOT hits called origin.");
        var entries = admin.ListEntries().ToArray();
        Check(entries.Length == 2 && entries.All(entry => entry.Kind == CacheEntryKind.Http &&
            entry.Storage == CacheEntryStorage.LocalMemory && entry.SizeBytes > 0), "HTTP AOT admin metadata failed.");
        var json = JsonSerializer.Serialize(entries, AotHttpJson.Default.Entries);
        Check(json.Contains("LocalMemory", StringComparison.Ordinal) && !json.Contains(callerA, StringComparison.Ordinal) &&
            !json.Contains(callerB, StringComparison.Ordinal) && !json.Contains("query-canary", StringComparison.Ordinal) &&
            !json.Contains("origin-", StringComparison.Ordinal), "HTTP AOT admin serialization leaked data.");
        _ = await admin.RemoveEntryAsync(entries[0].StoredKey);
        Check(await ReadAsync(callerA) == "origin-3", "HTTP AOT known-id invalidation failed.");
        await admin.ClearAsync();
        Check(await ReadAsync(callerA) == "origin-4", "HTTP AOT clear failed.");
        Console.WriteLine("PASS Native AOT HTTP private variants, HTTP-only administration and metadata JSON");

        async Task<string> ReadAsync(string caller)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://aot.invalid/response?token=query-canary");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller);
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Origin : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("origin-" + Interlocked.Increment(ref Calls)),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) } }
            });
    }
}

[JsonSerializable(typeof(CacheEntryInfo[]), TypeInfoPropertyName = "Entries")]
internal partial class AotHttpJson : JsonSerializerContext { }
