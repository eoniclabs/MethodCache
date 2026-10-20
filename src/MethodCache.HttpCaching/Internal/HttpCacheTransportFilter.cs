using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;

namespace MethodCache.HttpCaching.Internal;

// Runs after the named client's complete builder configuration, including handlers configured after AddHttpCaching.
internal sealed class HttpCacheTransportFilter : IHttpMessageHandlerBuilderFilter
{
    /// <inheritdoc />
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        var caches = builder.AdditionalHandlers.OfType<HttpCacheHandler>().ToArray();
        if (caches.Length == 0) return;
        foreach (var cache in caches) builder.AdditionalHandlers.Remove(cache);
        // AddHttpCaching for every client (ConfigureHttpClientDefaults) and again for this one, or twice for this one,
        // adds a handler each time. One cache is kept: a second one inside it would cache, count and report every
        // response again. Both read this client's options, so which one is kept makes no difference.
        for (var extra = 1; extra < caches.Length; extra++) caches[extra].Dispose();
        var kept = caches[0];
        // Run the cache after every additional handler, so caller identity and request transforms are visible.
        builder.AdditionalHandlers.Add(kept);
        // Caching added for every client keeps each client's entries apart
        if (!string.IsNullOrEmpty(builder.Name)) kept.UseClientName(builder.Name, builder.Services.GetRequiredService<HttpCacheClientStates>().For(builder.Name));
        var options = builder.Services.GetRequiredService<IOptionsMonitor<HttpCacheOptions>>().Get(builder.Name ?? string.Empty);
        if (options.KeepCookies) return;
        HttpMessageHandler? transport = builder.PrimaryHandler;
        while (transport is DelegatingHandler delegating) transport = delegating.InnerHandler;
        // Only when it changes anything: a handler shared between clients may already have sent requests
        var changed = false;
        if (transport is SocketsHttpHandler { UseCookies: true } sockets) { sockets.UseCookies = false; changed = true; }
        else if (transport is HttpClientHandler { UseCookies: true } client) { client.UseCookies = false; changed = true; }
        if (changed)
        {
            if (builder.AdditionalHandlers.OfType<HttpCacheHandler>().Any(cache => cache.ShouldWarnCookies()))
                builder.Services.GetRequiredService<ILogger<HttpCacheTransportFilter>>().LogWarning(
                    "HTTP caching disabled automatic transport cookies. Set KeepCookies to preserve cookie configuration and bypass response caching.");
        }
    };
}
